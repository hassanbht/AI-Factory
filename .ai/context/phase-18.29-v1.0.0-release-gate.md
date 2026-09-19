# Phase 18.29 — v1.0.0 Release Report

**Date:** 2026-09-19
**Decision:** `RELEASE BLOCKED`

No `v1.0.0` tag was created. No commit was made. Nothing was pushed. No CI release was
triggered. No GitHub Release was created. No image was published to GHCR.

Two release-blocking defects were found in the configuration and runtime topology that the
released containers actually ship with. Both were confirmed empirically against real
containers and the real production provider, not inferred from reading configuration.

---

## Source

| Field | Value |
|---|---|
| Final commit SHA | `fe5e69289b0fa610b3d90cee35d9b91e7521b3b6` |
| Short SHA | `fe5e692` |
| Branch | `main` (up to date with `origin/main`) |
| Release tag | **not created** — gate failed |
| Repository status | clean (`git status --porcelain` empty, `git diff --check` clean) |
| Remote | `https://github.com/hassanbht/AI-Factory.git` (reachable; `origin/main` == local HEAD) |
| Existing tags | `v0.1.0` only |

---

## Build

| Field | Value |
|---|---|
| Command | `dotnet build apps/AI.Factory.slnx --configuration Release` |
| Result | **Build succeeded** |
| Warnings | **0** |
| Errors | **0** |
| Elapsed | 21.68 s |
| SDK | .NET 10.0.401 |

---

## Tests

| Assembly | Passed | Failed | Skipped | Total | Duration |
|---|---|---|---|---|---|
| `AI.Factory.Api.Tests` | 304 | 0 | 0 | 304 | 24 s |
| `AI.Factory.Application.Tests` | 923 | 0 | 0 | 923 | 28 s |
| `AI.Factory.Worker.Tests` | 95 | 0 | 0 | 95 | 1 m 19 s |
| **Total** | **1322** | **0** | **0** | **1322** | — |

Command: `dotnet test apps/AI.Factory.slnx --configuration Release --no-build --blame-hang-timeout 120s`

Complete regression runs performed this phase: **1**. The release workflow's 3× stability
gate was **not** run, because the release was blocked before CI was reached. No test was
weakened, skipped, removed or altered.

---

## Database

| Field | Value |
|---|---|
| Migration status | `No changes have been made to the model since the last migration.` |
| Command | `dotnet ef migrations has-pending-model-changes --project apps/src/AI.Factory.Infrastructure --startup-project apps/src/AI.Factory.Api` |
| Pending model changes | **0** |
| SQLite mode | `Data Source=/var/lib/ai-factory/data/factory.db;Cache=Shared` (production), shared volume `ai_factory_production_data` |
| Operational verification | Migrator container ran to completion, exit code **0**. API and Worker containers both attached to the same SQLite database and exchanged state: the API persisted an `AgentRun`, the Worker in a separate container claimed and completed it. |

---

## Model

The production model configuration is defined in `ModelRouting` in
`apps/src/AI.Factory.Api/appsettings.json` and `apps/src/AI.Factory.Worker/appsettings.json`.
Neither `appsettings.Production.json` overrides it, so this **is** the released configuration.

| Role | Category | Configured model | Provider |
|---|---|---|---|
| Planner | `planning` | `auto/coding:free` | `openrouter` |
| Implementation | `coding` | `auto/coding:free` | `openrouter` |
| Repair | `debugging` | `auto/coding:free` | `openrouter` |
| Test | `testing` | `auto/coding:free` | `openrouter` |
| Review | `code-review` | `auto/coding:free` | `openrouter` |
| Documentation | `documentation` | `auto/coding:free` | `openrouter` |

Provider endpoint (production): `https://openrouter.ai/api/v1`, key from the
`OPENROUTER_API_KEY` environment secret via `EnvironmentSecretProvider`
(`SecretManagement.FailFastOnMissingSecret = true`).

**Fallback behavior — verified correct.** `openrouter/auto` and `openrouter/auto-beta` are
absent from the repository. An unmapped role fails closed:
`AgentOrchestrator.ResolveRoleCandidates` returns no candidates and the run is failed with
`"No explicit model or routing decision resolved for AgentType '<role>'. Silent DefaultModel
fallback is forbidden."` (`FailureCategory.Validation`). Model fallback is restricted to
`Provider`/`Model`/`Infrastructure` failure categories only and only to the role's next
configured candidate — a failed test or acceptance contract cannot be laundered into a retry.
With a single-entry catalog, `FallbackCount=0`; no fallback is available at all.

No secret value appears in this report.

---

## Runtime

Images built locally from the release Dockerfiles and run through the shipped production
compose topology (`docker-compose.yml` + `docker-compose.release.yml`, `up -d --no-build`).

| Check | Result |
|---|---|
| API container starts | **PASS** |
| API container stays running | **PASS** — `Up (healthy)` |
| `GET /health/live` | **PASS** — `200 Healthy` |
| `GET /health/ready` | **PASS** — `200 Healthy` |
| API reaches SQLite | **PASS** |
| Configuration loaded | **PASS** — `environment: Production` |
| Secret provider resolves secrets | **PASS** — fail-fast enabled, startup succeeded |
| API exposes no secrets | **PASS** — see Security below |
| Non-root execution (API) | **PASS** — `uid=1654(app) gid=1654(app)` |
| Migrator completes | **PASS** — exit code 0 |
| Worker container starts | **PASS** |
| Worker container stays running | **PASS** — `Up (healthy)` |
| Worker health indicator ready | **PASS** — `/tmp/ai-factory-health/worker.live` present |
| Worker connects to same database | **PASS** — claimed the API-created `AgentRun` |
| Worker claims work | **PASS** — `AgentRun a5fffa4e… claimed by worker.` |
| Non-root execution (Worker) | **PASS** — `uid=1654(app) gid=1654(app)` |
| **Hermes execution environment available** | **FAIL — BLOCKER 1** |
| **Production model executes** | **FAIL — BLOCKER 2** |
| Worker processes an AgentRun to success | **FAIL** (consequence of blockers 1 and 2) |

Deployment diagnostics from the running API container:

```json
{"applicationName":"AI.Factory.Api","environment":"Production","version":"1.0.0.0",
 "informationalVersion":"1.0.0","frameworkDescription":".NET 10.0.12",
 "osDescription":"Ubuntu 24.04.5 LTS","processArchitecture":"X64","isContainerized":true,
 "databaseProvider":"Sqlite","gitCommit":"fe5e692","gitBranch":"main","buildNumber":"0"}
```

Runtime version metadata flows correctly from the `BUILD_VERSION` build argument.

### Security boundary — PASS

* `OPENROUTER_API_KEY` value does not appear in any container log (14,526 bytes scanned).
* No secret-shaped token (`sk-or-v1-…`, `Bearer …`) appears in any container log.
* `/api/operations/deployment`, `/api/operations/configuration` and `/api/operations/database`
  responses contain no secret value.
* No credential was persisted to the database.
* `.env.live` is gitignored and untracked; the only committed env file is `.env.example`,
  which contains placeholders only.
* Both containers run as non-root UID 1654.

---

## Docker / GHCR

| Image | Tag | Local image ID | Platform |
|---|---|---|---|
| `ai-factory-api` | `v1.0.0` | `sha256:d4ca4267b033d0cc3bcf3197f91ff70eccfb1cf807016810a96dfc375c6a4ed9` | linux/amd64 |
| `ai-factory-worker` | `v1.0.0` | `sha256:373dc90c127702b29fdcc6888b743f086703f0162b090f87a5571fb33f9887ba` | linux/amd64 |

These are **local build IDs, not published registry digests**. Nothing was published to
GHCR — Workstreams H, I and J were not executed because the release was blocked before CI.
No immutable published digest exists for this release.

---

## GitHub Release

| Field | Value |
|---|---|
| Tag | **not created** |
| Release status | **not created** |
| Release commit | n/a |

---

## Release Blockers

### BLOCKER 1 — The Worker release container cannot execute Hermes at all

**Affected component:** `Dockerfile.worker`, `docker-compose.yml` (`worker` service),
`AI.Factory.Infrastructure.Agents.HermesAgentExecutor`.

**Cause:** infrastructure / deployment topology.

**Evidence** — a real `AgentRun` created through the containerized API
(`POST /api/agents/runs`, id `a5fffa4e-968f-400c-a890-5859142c28b6`) was claimed by the
containerized Worker and failed in 514 ms:

```
status: Failed
failureCategory: Agent
errorMessage: "Hermes could not be started: Failed to start process 'docker':
  An error occurred trying to start process 'docker' with working directory
  '/var/lib/ai-factory/workspace/37b997570f45490694a288984c78d6ed'. No such file or directory"
```

Confirmed directly against the image:

```
$ docker run --rm --entrypoint sh ai-factory-worker:v1.0.0 -c "command -v docker || echo 'docker: NOT INSTALLED'"
docker: NOT INSTALLED
```

`HermesAgentExecutor` executes Hermes by shelling out to `docker run nousresearch/hermes-agent`
via `SystemProcessRunner`. The Worker image is built from
`mcr.microsoft.com/dotnet/runtime:10.0` and installs no Docker CLI, and
`docker-compose.yml` mounts no Docker socket into the `worker` service (`grep docker.sock`
across all compose files and Dockerfiles returns nothing). The released Worker container can
therefore never start an agent.

Three further settings would each independently prevent a real LLM call even if the Docker
CLI were present, because nothing in the production configuration sets them:

* `Hermes.NetworkMode` is `"none"` — the Hermes container would have no network and could
  not reach any provider.
* `Hermes.Provider` is unset — no `--provider` flag is passed.
* `Hermes.ProviderApiKeyEnvironmentVariable` / `Hermes.ProviderApiKeySecretName` are unset —
  no provider API key is injected into the Hermes container.

**Smallest corrective action:** decide and then encode the supported production Worker
topology. Either

* (a) make the containerized Worker capable of running Hermes — add the Docker CLI to
  `Dockerfile.worker`, mount `/var/run/docker.sock` into the `worker` service, set
  `Hermes.NetworkMode` to a networked value, and set `Hermes.Provider`,
  `Hermes.ProviderApiKeyEnvironmentVariable` and `Hermes.ProviderApiKeySecretName` in
  `appsettings.Production.json`; or
* (b) declare the containerized Worker unsupported for agent execution and ship the Worker
  as a host process, in which case `docker-compose.yml` must stop presenting it as the
  production deployment.

Option (a) grants the Worker container control of the host Docker daemon, which is a
privilege escalation that needs an explicit decision rather than a silent fix — this is a
product/security call, not a mechanical one, so it is flagged rather than chosen here.

### BLOCKER 2 — The shipped production model id is not a valid model for the production provider

**Affected component:** `ModelRouting` in `apps/src/AI.Factory.Api/appsettings.json` and
`apps/src/AI.Factory.Worker/appsettings.json`.

**Cause:** configuration.

**Evidence** — every role routes to `auto/coding:free` on provider `openrouter`. Confirmed
from the running Worker container's own telemetry for the run above:

```
AgentRunModelResolved AgentType=Implementation Category=coding PrimaryModel=auto/coding:free FallbackCount=0
AgentRunModelTelemetry Model=auto/coding:free Provider=openrouter FallbackUsed=False
```

and from the persisted structured result:

```json
"metadata": {"AgentType":"Implementation","Category":"coding","PrimaryModel":"auto/coding:free",
             "SelectedModel":"auto/coding:free","Provider":"openrouter","FallbackUsed":"false"}
```

A live request to the production-configured provider with the production-configured model id:

```
POST https://openrouter.ai/api/v1/chat/completions   model="auto/coding:free"
HTTP 400  {"error":{"message":"auto/coding:free is not a valid model ID","code":400}}
```

`auto/coding:free` is absent from OpenRouter's catalog (447 models enumerated). It is an
OmniRoute gateway virtual model inherited from Phase 18.1.7, when Hermes was pointed at a
local OmniRoute gateway via an `OPENROUTER_BASE_URL` override. The provider configuration
later moved to real OpenRouter, but `ModelRouting` did not move with it. The models actually
qualified in the live runs — `deepseek/deepseek-v4-flash-0731:free` for planning/review and
`qwen/qwen3-coder` for coding/debugging/testing — exist only in the gitignored, test-process-only
`.env.live`, never in shipped configuration. Both were verified serving `HTTP 200` in this
session against the same production endpoint.

`ModelRoutingOptions.Validate()` did not catch this: it checks only that the catalog is
internally coherent and free of the six known placeholder ids. `auto/coding:free` passes
every structural check while being unservable.

**Smallest corrective action:** set `ModelRouting` in `appsettings.Production.json` (API and
Worker) to the OpenRouter model ids already qualified by the live runs, and extend the
existing startup validation with a preflight that rejects a configured model id the
configured provider does not list — so an unservable model is refused at startup rather than
shipped and discovered at first agent run.

---

## Workstream status

| Workstream | Status |
|---|---|
| A — Final production configuration verification | **FAIL** — blocker 2 (fail-closed behavior and secret handling verified correct) |
| B — Production model live smoke | **FAIL** — configured model rejected `HTTP 400` by the configured provider |
| C — Real Docker runtime verification | **FAIL** — API passes fully; Worker starts and claims work but has no Hermes execution environment |
| D — End-to-end production container smoke | **FAIL** — lifecycle cannot proceed past agent execution |
| E — Release version integrity | **NOT COMPLETED** — see below |
| F — Full final regression | **PASS** — 0 warnings, 0 errors, 1322 passed, 0 failed, 0 skipped, 0 pending migrations |
| G — Git cleanliness & release boundary | **PASS** — tree clean, no secrets tracked, no stray artifacts |
| H — CI/CD release | **NOT RUN** — blocked |
| I — GHCR verification | **NOT RUN** — blocked |
| J — Published artifact runtime verification | **NOT RUN** — blocked |
| K — Release tag and GitHub Release | **NOT RUN** — blocked |
| L — Final production smoke | **NOT RUN** — blocked |

### Workstream E note

The repository's single version authority is the `VERSION` file, consumed by
`apps/Directory.Build.props` and validated against the pushed tag by `release.yml`
("Validate tag matches VERSION"). It currently reads `0.1.0`. Releasing `v1.0.0` requires
bumping it to `1.0.0` — otherwise `release.yml` fails the tag-validation step before any
build work, which is correct behavior. This bump was deliberately **not** made, because
committing a version bump for a release that is blocked would leave the repository claiming
a version it never shipped. No second version authority exists, and no stale `rc-1` tag is
referenced by any release path (`rc-1` exists only as a local Docker image tag from a prior
phase, not in `VERSION`, compose, or CI).

---

## GO / NO-GO Decision

# `RELEASE BLOCKED`

The build, the full regression and the migration state are all green, the repository is
clean, the security boundary holds and the API container is production-ready. The release is
blocked solely because the released artifacts cannot perform the system's core function: the
Worker container has no Hermes execution environment, and the model configuration it ships
with is rejected by the provider it ships pointing at.

Both defects are in configuration and deployment topology, not in orchestration logic. The
workflow engine, state persistence, recovery, fail-closed routing and security boundary all
behaved correctly under real container execution.

---

## Next Step

Resolve blockers 1 and 2, then re-run Phase 18.29 from Workstream A. Do not proceed to
`Phase 18.30 — Post-Release Soak, Monitoring & Stabilization`; there is no release to soak.
