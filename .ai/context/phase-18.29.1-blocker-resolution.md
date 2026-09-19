# Phase 18.29.1 — Production Blocker Resolution & v1.0.0 Release Re-Validation

**Date:** 2026-09-19

Phase 18.29 ended `RELEASE BLOCKED` on two defects. Resolving them by running the real
containers — rather than reading configuration — exposed four more, each hidden behind the
previous one. All six are fixed, and the complete Golden Lifecycle now reaches `Completed`
through the released containers on the configured production models.

The single root cause behind almost all of it: **the containerized Worker had never
actually executed the Factory's core work.** Every prior "golden lifecycle" was produced by
an in-process test host, where the Docker CLI, the .NET SDK, the workspace volume and the
argument-size limit were all someone else's problem.

---

## Blockers resolved

### 1. The Worker container could not execute Hermes

`HermesAgentExecutor` runs agents as `docker run nousresearch/hermes-agent`. The Worker
image had no Docker CLI and the deployment mounted no socket, so every AgentRun failed in
~500ms with *"Failed to start process 'docker'"*.

Fixed in three parts, because the CLI alone was not enough:

* `Dockerfile.worker` copies `/usr/local/bin/docker` from `docker:29-cli` — client only,
  never a daemon.
* `docker-compose.yml` mounts the host Docker socket read-only, with `group_add` for the
  socket's owning group so the non-root Worker can open it. **The security implication is
  documented in place and is not downplayed: a process that can reach the host Docker
  socket is effectively root on that host.**
* `Hermes.NetworkMode` → `bridge`, plus explicit `Provider`,
  `ProviderApiKeyEnvironmentVariable` and `ProviderApiKeySecretName`. Production had
  inherited `NetworkMode: none` and no provider at all, so a real LLM call was impossible
  even once the CLI existed.

**A bind mount would have silently done the wrong thing.** A `docker run -v` path issued
over a mounted socket is resolved by the *host* daemon, where the Worker's
`/var/lib/ai-factory/workspace/<id>` does not exist — Docker would create an empty host
directory and the agent would run against nothing. `HermesOptions.WorkspaceVolumeName`
addresses the workspace as a subpath of the shared named volume instead
(`--mount type=volume,volume-subpath=…`), re-checked through `WorkspacePathGuard` so a
workspace outside the root is refused rather than mounted.

File ownership uses the Hermes image's supported `HERMES_UID`/`HERMES_GID` remap.
`docker run --user` is the obvious-looking approach and is wrong: the image's s6-overlay
supervision tree must bootstrap as root and refuses a pinned arbitrary UID. Verified
against the real image; asserted in a test so it is not reintroduced as a simplification.

### 2. The shipped production model id was not servable

Every role routed to `auto/coding:free` on `openrouter` — an OmniRoute gateway virtual
model inherited from Phase 18.1.7 that OpenRouter rejects with HTTP 400
(*"auto/coding:free is not a valid model ID"*).

`ModelRoutingOptions.Validate()` could not catch it: it is offline and structural, and the
id is well-formed, references a known provider and resolves in every category. So a
provider-aware preflight was added — `IProviderModelCatalog` +
`OpenAiCompatibleModelCatalog` + `ModelRoutingPreflight` — which checks every configured
model/provider pair against the provider's live catalog before the host accepts work.

It never substitutes a model. An unservable id aborts startup with an explicit message
naming the model, the provider and the affected roles. A provider **outage** is reported
and logged but never aborts startup, because an outage proves nothing about a model id and
turning every provider blip into an unbootable deployment would be worse than the problem.

### 3. The Worker container could not build or test anything

`dotnet --list-sdks` returned nothing: the image was based on `dotnet/runtime:10.0`, but
`DotNetBuildExecutor`/`DotNetTestExecutor` shell out to `dotnet build` as the verification
gate. Every WorkItem failed with *"No .NET SDKs were found"* (exit 155).

The consequence was worse than a plain failure: the gate rejected the work, the repair loop
dispatched, and **the repair agent tried to fix an image defect by downloading an entire
.NET SDK into the project workspace** — burning bounded attempts on something no generated
code could ever fix.

Base switched to `dotnet/sdk:10.0` (which also defines `APP_UID`, so non-root is
preserved) with a writable `HOME` for the NuGet cache. Image grows 365MB → 1.43GB; that is
the honest cost of what the Worker actually does. The API image is untouched.

### 4. Verification could not find a project one directory down

The gate passes the workspace *root* to `dotnet build`, and MSBuild does not recurse. A
project in a subdirectory — the layout an agent naturally produces, and the layout this
repository itself uses — failed with MSB1003 and sent the repair loop after a framing
problem rather than a code problem.

`MsBuildTargetResolver` is deliberately conservative: a file path, or a root that already
contains a project/solution, is returned **untouched** so existing behavior is unchanged; a
nested target is chosen only when unambiguous; genuine ambiguity is left to MSBuild rather
than guessed, because silently verifying one of several projects would report a green gate
for something nobody chose.

### 5. Agents were graded against a rubric they were never shown

`WorkItemAcceptanceVerifier` evaluates workspace-relative paths, but neither the execution
Objective nor the repair Objective ever stated the criteria or what they were relative to.
An agent would produce a perfectly good project one directory down, the contract would look
for it at the root, and the WorkItem failed — then the repair loop exhausted its budget,
because its objective carried only a truncated failure summary and still never stated the
rule.

Both objectives now render the criteria from the **same parser the verifier uses**, so the
stated contract cannot drift from the enforced one, and say explicitly that paths are
relative to the working-directory root. Absent or legacy free-text criteria produce no
section rather than an invented one — presenting free text as a checkable contract would
promise a gate that never runs.

### 6. The review stage could not run at all on a large workspace

The agent's `dotnet` toolchain treated the shared workspace as its home and vendored an
entire SDK into it. `FileSystemArtifactRecorder` then recorded thousands of artifact rows,
and the review prompt built from them — delivered to Hermes as a `docker run -z <prompt>`
**argument** — exceeded `ARG_MAX`: *"Failed to start process 'docker': Argument list too
long"*. The independent review did not degrade; it became impossible.

Fixed at both ends: Hermes now gets `HOME`/`DOTNET_CLI_HOME`/`NUGET_PACKAGES` pointed at
its own `/opt/data` volume so the toolchain stops unpacking into the Project workspace, and
the changed-file list is bounded at 200 with an explicit *"… and N more file(s) not
listed"* marker — a reviewer told nothing would assume it saw everything and could report
"no findings" over files it never read.

### Additionally: a real DbContext concurrency defect

The heartbeat loop and the orchestrator resolved services from the **same** DI scope, so
they shared one `FactoryDbContext` — and they overlap by design, since the heartbeat exists
to prove a long execution is still alive. The first containerized Implementation run hit it
writing workspace artifacts mid-heartbeat: *"A second operation was started on this context
instance before a previous operation completed."*

Pre-existing (Phase 18.4); the container path merely made it reachable. The heartbeat now
owns its own scope. The test asserts the scope boundary directly rather than racing
timings, and was verified to fail against the unfixed code before being accepted.

### Additionally: the API could not see the workspaces it provisions

The API owns workspace provisioning (`POST /api/projects/{id}/source`) while the Worker
owns execution, but only the Worker mounted the workspace volume. The API created each
Project's workspace inside its own container filesystem and the Worker then failed every
Implementation run with *"workspace does not exist or is not accessible"* — two halves of
one contract writing to two different filesystems. Both now share the volume.

---

## Configuration decisions (explicitly authorized)

### Agent execution budget: 5 → 15 minutes

Phase 18.20 already measured the 5-minute budget as too tight for real agent work and
recorded that changing it needed a deliberate product decision rather than a silent bump.
A live containerized run had a Documentation WorkItem cancelled at **299,930ms** — the 300s
limit — with the agent still working normally.

Raised across **every** layer that can cut an execution short, because the smallest one
silently wins: `Hermes.TimeoutSeconds` (900s, the layer that actually fired),
`ExecutionLimits.AgentExecutionTimeoutMinutes`, the `AgentExecution` resilience profile's
`AttemptTimeout`/`OperationTimeout`, and `AgentWorker.ExecutionTimeout`.

This is an execution **budget**, not a gate. No test, acceptance criterion,
structured-result validation or release gate was relaxed.

### Production coding models

`qwen/qwen3-coder` emitted a `.csproj` MSBuild rejected outright (MSB4025, *"'\' is an
unexpected token"*) on a trivial class library, and the bounded repair budget was exhausted
without recovering.

Replaced with `anthropic/claude-sonnet-5` for coding/debugging/testing/documentation and
`openai/gpt-5.1` for review. Both were asked for the exact artifact qwen botched and both
returned clean, valid XML; both were then independently verified by the startup preflight
against the live provider catalog. Review routes to a different vendor than implementation
so the implementing model does not grade its own work. Planning stays on the free DeepSeek
route, which produced correct, materializable plans in every live run.

---

## Golden Lifecycle — PASSED

Real containerized API + Worker, configured production models, `https://openrouter.ai/api/v1`.

| Field | Value |
|---|---|
| Project | `da49fed8-3ac2-427b-ac7b-74853c3a8e1d` |
| Duration | 7m24s (11:10:19 → 11:17:43 UTC) |
| Final state | **Completed** |
| WorkItems | 3 of 3 Completed, **`repair=0`** on every one |
| Fallback used | `False` on every run |

```
Create Project → Source → Start → Analyzing → Specification → Architecture
→ Planning → Planner (deepseek, 74.1s)
→ 3 WorkItems → Implementation ×2 (claude-sonnet-5, 29.8s / 29.5s)
→ Documentation (claude-sonnet-5, 34.2s)
→ real dotnet build + acceptance criteria (2/3/2 evaluated, all Passed)
→ Reviewing (gpt-5.1) → HumanReview → explicit approval → Completed
```

The project **stopped at HumanReview** and only reached Completed after an explicit approve
call — the human-approval contract holds.

Produced working code, with a clean workspace (`Calculator.cs`, `README.md`,
`golden-v1.csproj`, plus normal `bin`/`obj`; no vendored SDK):

```csharp
namespace GoldenV1
{
    public static class Calculator
    {
        public static int Add(int a, int b) => a + b;
    }
}
```

An earlier run on the same code also demonstrated the **repair** path end to end:
Implementation → verification failed → Repair → `WorkItemAcceptancePassed`,
`VerificationGateCompleted Status=Passed`.

---

## Deterministic regression — 3 consecutive green runs

| Assembly | Passed | Failed | Skipped |
|---|---|---|---|
| `AI.Factory.Api.Tests` | 304 | 0 | 0 |
| `AI.Factory.Application.Tests` | 952 | 0 | 0 |
| `AI.Factory.Worker.Tests` | 96 | 0 | 0 |
| **Total** | **1352** | **0** | **0** |

Build: **0 warnings, 0 errors**. EF Core: **0 pending model changes**.

30 tests were added this phase. One existing assertion changed: `ContainerConfigurationTests`
pinned the Worker base image to `runtime:10.0`, which the deliberate SDK switch invalidates.
**That is not a weakening** — every other assertion in it is untouched, and two were *added*
(the base must provide an SDK; the image must carry the Docker CLI), so the test now pins
the blocker-1 and blocker-3 fixes against regression.

---

## Security audit — clean

* `OPENROUTER_API_KEY` absent from 113,083 bytes of container logs; no token-shaped strings.
* Absent from `/api/operations/deployment`, `/api/operations/configuration`,
  `/api/operations/database`, and from AgentRun structured metadata.
* No real credential tracked in the repository. The only `sk-or-v1-` matches are deliberate
  **fake** fixtures in the test that asserts sanitization.
* `.env.live` gitignored and untracked; the only committed env file is `.env.example`.
* Both containers non-root (UID 1654) — preserved on the new SDK base.
* Provider/model metadata remains available for diagnostics.

---

## Docker artifacts

Both images scan **clean** for CRITICAL/HIGH (Trivy).

| Image | Local ID | Size | Platform |
|---|---|---|---|
| `ai-factory-api:v1.0.0` | `sha256:e71b9555d244cf94bcc5d7999f1623f3658e23889aafaca99b56235a09aac5ee` | 412MB | linux/amd64 |
| `ai-factory-worker:v1.0.0` | `sha256:f9e7402970391d0371e1af5c37a51f9e16c31b9c375d4aaae549f5adf61d7d97` | 1.43GB | linux/amd64 |

`docker:28-cli` was rejected: its client carries 22 HIGH/CRITICAL Go stdlib CVEs (1
CRITICAL — TLS session-resumption certificate validation) and **correctly failed the
release pipeline's Trivy gate**. `docker:29-cli`'s client scans clean; its `docker-buildx`
plugin still carries HIGH findings and is deliberately not copied, since the Worker issues
`docker run` and never builds images. The gate was not weakened to accommodate the
dependency.

These are local build IDs. Immutable published digests are recorded by the release
pipeline.
