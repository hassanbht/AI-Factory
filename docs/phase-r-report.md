# Phase R Result — Production Readiness, Delivery Governance & Operational Hardening

Date: 2026-10-08
Status: **PREFLIGHT ONLY — PHASE R ENTRY BLOCKED (Phase Q not closed)**
Reference: Phase R Specification (`prompts/Phase R — Production Readiness, Delivery Governance & Operational Hardening.md`),
Phase P.3 Report (`docs/phase-p3-report.md`), Phase Q Report (`docs/phase-q-report.md`)

---

## 0. Header (required first output)

```text
PHASE Q FINAL GATE: <none — docs/phase-q-report.md contains no gate value>
                    status line reads: "PENDING — BENCHMARK RUNNING"; §20 "Final phase gate" = "TBD."
PHASE R ENTRY: BLOCKED
REASON: Phase Q never completed. Its 12-run live matrix stopped after Q-A (3 of 12 runs),
        no summary.json was produced, no parser output exists, and the report is a stub whose
        results sections are all "TBD". The Phase R precondition — "starts only after Phase Q
        has been fully closed and its final report exists" — is therefore unmet. Factory-owned
        blockers were also confirmed independently of the missing matrix: (B1) execution-report
        schema drift discards agent run metadata on every live run; (B2) two intermittent
        load-dependent test failures make the canonical regression command red in 2 of 3 runs;
        (B3/B4) the benchmark harness cannot represent a partial phase and hides a timed-out
        attempt's spend.
```

This report does **not** rewrite, reinterpret or re-certify the Phase Q outcome. No Phase R
production-readiness implementation was performed: per the Phase R specification §1, a phase that
cannot reach a valid GO gate must stop and produce a preflight report instead.

---

## 1. Phase Q final-gate inspection (evidence)

### 1.1 What the report says

`docs/phase-q-report.md` is a **stub**. Header: `Status: PENDING — BENCHMARK RUNNING`.
Sections 1, 7, 8, 9, 10–16, 19, 20 and 21 are `(TBD …)`; §20 `Final phase gate` is `TBD.` No gate
value of the three allowed values exists anywhere in the document.

### 1.2 What the artifacts on disk say

Evidence root: `TestResults/PhaseQ/`.

| Artifact | Timestamp | Content |
| --- | --- | --- |
| `baseline.txt` | 2026-10-07 20:52 | repo `9b02037`, `gitStatusLines=4`, dotnet 10.0.401, hermes `0.20.6`, explicit model, `factoryContainers=0` |
| `pilot-Q-A-1.txt` / `.console.log` | 2026-10-07 21:05 | pilot run, `workItem.final=Completed` |
| `Q-A-1.txt` / `.console.log` | 2026-10-07 21:21 | `workItem.final=Completed`, validation/review/artifact/cleanup checks all recorded |
| `Q-A-2.txt` / `.console.log` | 2026-10-07 21:51 | `workItem.final=Completed` **after 1 bounded retry** (attempt 1 `TimedOut`) |
| `Q-A-3.console.log` | 2026-10-07 22:01 | run completed (`Test Run Successful`, 10.5 min); `workspace.missingRequired=[]`, artifacts present |
| `Q-A-3.txt` | **missing** | the per-run evidence file was never written for Q-A-3 |
| `summary.json` | **missing** | the harness writes it only after all 12 runs |

So the matrix is **3 of 12 live runs** (Q-A × 3 repetitions) plus one pilot. Q-B, Q-C, Q-D and
repetition data beyond Q-A do not exist. `scripts/parse-phase-q-results.ps1` cannot run at all in
this state (it reads `summary.json`, and `$ErrorActionPreference = "Stop"`), and
`scripts/run-phase-q.ps1` has no resume path — a re-run repeats the full 12-run matrix.

The harness stopped without writing a failure record, and `D:\Project\AI-Factory` (the root named in
`AGENTS.md` and the build path embedded in every Q console log, e.g.
`AI.Factory.Domain -> D:\Project\AI-Factory\apps\...`) **no longer exists**; the tree now lives at
`C:\AI-Factory`. The most consistent reading is that the benchmark was interrupted by the checkout
relocation, not by a recorded Factory crash — but this is an inference, and it is recorded as such.
No gate may be inferred from it.

### 1.3 What the completed Q-A runs do prove (retained, not discarded)

All three Q-A repetitions plus the pilot reached `workItem.final=Completed` through the full Factory
path with the explicit certified configuration, and each recorded `validationRun success=True`,
`reviewRun blocksCompletion=False`, `workspace.missingRequired=[]`,
`workspace.forbiddenRuntimeState=[]`, `workspace.filesContainingCredential=0`,
`containers.after15s factoryOwned=0` and positive `artifact.check … present=True`.

| Run | Wall | AgentRun attempts | Measured cost (Hermes estimate) |
| --- | --- | --- | --- |
| pilot-Q-A-1 | 699 s | 1 (Succeeded) | $0.007960 |
| Q-A-1 | 822 s | 1 (Succeeded) | $0.037314 |
| Q-A-2 | 1725 s | 2 (TimedOut → Succeeded) | $0.007292 reported (see F-3) |
| Q-A-3 | 605 s | 1 (Succeeded) | $0.007685 |

This is **partial** evidence for one task class, not a Phase Q certification, and it is not used here
to manufacture a GO gate.

---

## 2. Blocking findings

### B1 — FACTORY_DEFECT: agent execution-report schema drift discards run metadata (confirmed)

Observed in **all three** completed Q-A runs (`WorkItemExecutionMetadataUnavailable … Path: $.execution.tests[0]`):

```text
The JSON value could not be converted to System.String.
Path: $.execution.tests[0] | LineNumber: 0 | BytePositionInLine: 294.
```

Cause: the Hermes runtime's own documented execution report emits `tests` as an array of **objects**
(`"tests":[{"name":"Add_ReturnsSum","result":"passed"}, …]` — verbatim in all three evidence files),
`artifacts` is an empty array, while `WorkItemExecutionResultMapper.ExecutionPayload` declares `Tests`
as `List<string>?`. `System.Text.Json` therefore throws `JsonException`, which
`AgentRunWorkItemSync.InterpretExecutionReport` converts to `ExecutionMetadataUnavailable`.

Impact and non-impact, stated precisely:

- **Safety contract holds.** By design (`AgentRunWorkItemSync` §4 / Phase 18.20) an unusable report
  sends the WorkItem to `Testing` and the Factory verifies independently — the run is not trusted,
  not failed, and not dead-ended. The agent's self-reported test results were never trusted anyway.
- **Auditability degrades.** The structured result is unreadable to the Factory on *every* live run,
  so the run's own claims, artifact list and notes are lost from persisted state — exactly the
  delivery-audit chain Phase R §8/§15 must certify.
- Severity: **medium** (auditability, not safety). Classification: `FACTORY_DEFECT`.

### B2 — FACTORY_DEFECT: intermittent failures in Factory-owned test code break the regression baseline (confirmed)

Two distinct tests failed across three full-solution runs of the **unmodified** default command
(`dotnet test apps/AI.Factory.slnx`). Both pass 3/3 in isolation and failed only under
solution-wide parallel load:

**B2a — test fixture file-sharing race.** Run #1:

```text
Failed AI.Factory.Application.Tests.Execution.CliProcessTreeLifecycleTests
       .Cancellation_DuringActiveExecution_KillsTheTree_AndIsCancelledNotFailedOrTimedOut(runtime: ClaudeCode)
System.IO.IOException : The process cannot access the file
  'C:\Users\…\aif-phasen-7119ebf85c524d1ba7cd9409fdbf5c5d\probe\pids.txt' because it is being used by another process.
   at … CliProcessTreeLifecycleTests.PidsRecorded() … line 158
```

The fixture's fake CLI writes the probe with a non-atomic
`[IO.File]::WriteAllText("$d\pids.txt", …)` (`CliProcessTreeLifecycleTests.cs:91-92`, Windows
PowerShell child process) while the test polls the same file with `File.ReadAllLines`
(`CliProcessTreeLifecycleTests.cs:158`). Under solution-wide parallel load the reader intermittently
loses the file-sharing race. Product code is not involved (fake CLI + test helper only).

- Reproducibility: **3/3 PASS in isolation** (`--filter …Cancellation_DuringActiveExecution`),
  1 FAIL in full-solution run #1, 0 FAIL in runs #2 and #3 → **intermittent**.
- Classification: `FACTORY_DEFECT` (test fixture). Phase Q's own harness ran single-project
  `dotnet test <csproj>` and so would not have exercised this path under full-solution load.

**B2b — API test helper masks the real response.** Run #3:

```text
Failed AI.Factory.Api.Tests.ProjectsAndTasksApiTests.UpdateTaskStatus_ValidTransition_Returns200
System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.
  at System.Text.Json.JsonElement.GetProperty(String propertyName)
  at ProjectsAndTasksApiTests.CreateProjectAsync(String name, Boolean initializeSource) … line 30
```

`CreateProjectAsync` parses `POST /api/projects` with `body.GetProperty("id")` **without asserting the
status code first** (`ProjectsAndTasksApiTests.cs:27-30`), so any non-2xx/error/empty payload surfaces
as an unrelated `KeyNotFoundException` and the actionable body is discarded. Reproducibility:
**3/3 PASS in isolation** (`--filter …UpdateTaskStatus_ValidTransition`), 1 FAIL in run #3,
0 FAIL in runs #1 and #2.

Severity of B2a+B2b: **medium** — neither touches product behaviour, but together they make the
repository's canonical regression command intermittently red (**2 of 3 full-solution runs had ≥1
failing test**), which blocks Phase R §22's "three consecutive green runs" gate — the phase's primary
evidence mechanism. B2b's root cause is **not determined**: because the helper drops the response
body, it cannot be distinguished from a transient API-side error (e.g. a 5xx under parallel load).
Classification: `FACTORY_DEFECT` (test code), root cause `UNKNOWN` until the response is captured.

### B3 — FACTORY_DEFECT (evidence): Phase Q tooling cannot represent a partial or interrupted phase

`scripts/run-phase-q.ps1` writes `summary.json` only after all 12 runs and has no resume; the
per-run evidence file for Q-A-3 was never written even though the run itself completed (its full
evidence exists only inside `Q-A-3.console.log`); `scripts/parse-phase-q-results.ps1` hard-fails
without `summary.json`. An interrupted benchmark therefore leaves **no machine-readable result at
all**. Severity: low–medium (process/evidence integrity). Classification: `FACTORY_DEFECT`.

### B4 — OBSERVABILITY_GAP: a timed-out attempt's model spend is invisible

In Q-A-2 attempt 1 (`status=TimedOut`, `durationS=1188`) the evidence records
`promptTokens=NOT REPORTED`, `completionTokens=NOT REPORTED`, `totalTokens=NOT REPORTED` and no
`CostUsd`. The harness's spending ceiling is computed **from parsed evidence cost**, so a run that
burns ~15 minutes of model time contributes $0 to the ceiling. This is a direct Phase R §9
(cost/resource governance) concern: `Usage`/cost telemetry must fail loudly enough that a ceiling
cannot silently under-count. Severity: medium. Classification: `FACTORY_DEFECT`.

### B5 — EXTERNAL_RUNTIME_CONSTRAINT: the Hermes container ships no .NET SDK

Every Q-A run's agent notes state that no `dotnet` was present (`Installed .NET SDK 10.0.401 to
/opt/data/.dotnet … DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 … libicu missing`) and that the
project's literal `net10.0` suite could therefore not be run inside the runtime; Q-A-1's agent
verified on a retargeted net9.0 copy instead. The adjacent Factory Worker image does have
`mcr.microsoft.com/dotnet/sdk:10.0` (`Dockerfile.worker:34`), so the *Factory's* verification gate is
unaffected — but the runtime pays a per-run SDK install (~10–29 min wall, 2 of 4 logged attempts hit
the 15-minute attempt timeout at 911 s / 1188 s). This inflates cost, latency and timeout pressure in
Phase R §9/§10 territory. Classification: `EXTERNAL_RUNTIME_CONSTRAINT` **with a Factory configuration
component** (execution profile / workspace image should pre-provision the project toolchain).

### B6 — Environment facts that also cap what Phase R can evidence right now

- **Docker daemon is not running** on this host (`npipe:////./pipe/dockerDesktopLinuxEngine` absent).
  No live Hermes run can be started, and no container/volume/network audit is possible (§23).
  Factory-owned containers observed: **0** (nothing to clean up; nothing verifiable).
- **Checkout relocated** `D:\Project\AI-Factory` → `C:\AI-Factory`; all Q evidence embeds `D:` build
  paths. Build and tests were re-established here (below).
- `.env.live` exists at the repo root and is correctly git-ignored (`.gitignore:210`) — present but
  never loaded by the default test path (verified: no credential reads in the default run).

---

## 3. Baseline re-established on this checkout

- `dotnet build apps/AI.Factory.slnx` → **0 errors, 3 warnings** (the 3 pre-existing `CS0105`
  duplicate-using warnings reported at P.3), 22.8 s.
- Full-solution default regression, **two full runs** on 2026-10-08 (`TestResults/PhaseR/`):

| Run | Worker | Api | Application | TOTAL | Failed |
| --- | --- | --- | --- | --- | --- |
| #1 (`regression-preflight.log`) | 132 p / 39 sk | 328 p / 1 sk | 1455 p / **1 F** / 5 sk | 1961 | **1** (B2a) |
| #2 (`regression-preflight-2.log`) | 132 p / 39 sk | 328 p / 1 sk | 1456 p / 5 sk | 1961 | 0 |
| #3 (`regression-preflight-3.log`, TRX in `Run3/`) | 132 p / 39 sk | 327 p / **1 F** / 1 sk | 1456 p / 5 sk | 1961 | **1** (B2b) |

Run #2 = **1961 total / 1916 passed / 0 failed / 45 skipped**. Run #3 re-parsed with the canonical
`scripts/parse-trx.ps1` → `TOTAL=1961 passed=1915 failed=1 skipped=45 skipsWithoutGateReason=0`,
i.e. **every skip carries a gate reason** from `apps/tests/Shared/TestGating.cs`
(`LIVE OPT-IN NOT ENABLED` / `ENVIRONMENT BLOCKED`) and no un-gated test was silently skipped.
Reconciliation against the Phase P.3
baseline (1960 / 1921 / 0 / 39) is exact and fully explained:

```text
+1 total     the new Phase Q live test (skipped by default — unchanged, correctly gated)
+6 skipped   Docker-gated tests (5 × DockerLifecycleIntegrationTests + OpenHandsStateIsolationLiveTests)
             now report ENVIRONMENT BLOCKED because the Docker daemon is down; they passed at P.3
-1 passed    the B2 fixture flake in run #1 (intermittent)
```

**The Phase R §22 gate cannot be attempted until B2 is fixed and a three-run consecutive green result
is obtained with a known Docker state** (the Docker-gated delta above means a run's skip set is not
comparable across hosts/daemons).

---

## 4. Phase R scope status

No Phase R objective (§2), governance change (§5–§7), audit hardening (§8), resource control (§9),
recovery/idempotency change (§10–§11), workspace change (§12), credential change (§13), observability
change (§15), state change (§16) or API change (§17) was implemented. Per §1, implementation must not
begin while the entry gate is blocked, and per §29 no Phase R deliverable may be claimed without its
regression evidence. Nothing in §24's change discipline was exercised because no code was modified.

Note for the next attempt: §1's "proceed with the Factory-owned hardening that is independent of the
blocked external runtime" path applies to an entry status of **LIMITED**, which requires a completed
Phase Q whose *only* remaining limitation is external. That precondition is not met here.

---

## 5. Actions required to close Phase Q and re-enter Phase R

0. **Fix B2** — make the probe write atomic (write-then-replace) or the reader retry; assert the
   HTTP status code in the API helper and include the response body in the failure message. These
   two Factory-owned test defects are the ones standing between the repository and any clean
   three-run baseline.
1. **Fix B1** — make the execution-result mapper tolerant of the runtime's actual report shape
   (`tests` / `artifacts` as objects *or* strings), with regression tests for both shapes, so the
   Factory stops discarding run metadata. Must not change the fail-safe semantics of an *unusable*
   report.
2. **Fix B2** — make the fixture's probe write atomic (write-then-replace) or make the reader
   retry/`FileShare.ReadWrite`, then prove it with three consecutive full-solution green runs.
3. **Fix B3/B4** — make the benchmark harness resumable and dump partial results/evidence per run;
   surface non-reported usage from a timed-out attempt as an explicit ceiling-integrity warning.
4. **Resolve B5** — decide the runtime toolchain policy (pre-provision the SDK in the execution
   workspace image, or state the install-per-run behavior as an accepted, documented cost), then
   re-measure.
5. **Restart Docker**, confirm `factoryContainers=0`, and re-run `scripts/run-phase-q.ps1` to
   completion (all 12 runs), then `scripts/parse-phase-q-results.ps1` and write the Phase Q gate from
   real numbers. Billed cost: ~$0.06 / 4 runs measured for Q-A; the 12-run matrix at the Q-A rate is
   well inside the configured `PHASE_Q_MAX_SPEND_USD = 2.00`, but Q-A is the cheapest task class.
6. **Then** re-enter Phase R with a recorded Q gate.

Live model execution was deliberately **not** repeated for this preflight (§21: do not re-run billed
experiments unnecessarily) — no credential was read and no provider call was made.

---

## 6. Final gate

```text
PHASE R FINAL GATE: BLOCKED — FACTORY DEFECT
```

Factory-owned defects block entry: **B1** (execution-report schema drift discards per-run metadata on
every live execution), **B2** (two intermittent load-dependent test failures make the canonical
regression command red in 2 of 3 runs) and **B3/B4** (harness cannot represent a partial phase; a
timed-out attempt's spend is invisible). Not one of them is an external constraint. Docker being down
(B6) is a separate, genuinely external limitation that additionally prevents any live evidence today.

## 7. Recommended next phase

**Do not start Phase S.** The next task is **Phase Q remediation and closure**: fix B1 (metadata
schema drift) and B2 (both intermittent test failures), harden B3/B4, then — with the Docker daemon
up — re-run the full 12-run matrix and replace the stub's `TBD` result fields with the resulting real
numbers and gate. Phase R may re-enter only once that gate reads `GO FOR PHASE R` (or `LIMITED` for a
limitation that is genuinely external-only).

Splitting the work this way keeps §24's change discipline intact: the two test-defect fixes and the
metadata-mapper fix are verifiable without any billed model execution, so they can be completed and
proved green before a single live run is repeated.

## 8. Certification table

| Area | Result | Evidence |
| --- | --- | --- |
| Phase Q entry | **BLOCKED** | `docs/phase-q-report.md` is a stub (`PENDING — BENCHMARK RUNNING`, §20 = TBD); 3 of 12 runs in `TestResults/PhaseQ/`; no `summary.json` |
| Build | PASS | `dotnet build apps/AI.Factory.slnx` → 0 errors, 3 pre-existing warnings |
| Lifecycle | NOT RUN (entry blocked) | no Phase R lifecycle test executed |
| Governance | NOT RUN (entry blocked) | — |
| Human approval | NOT RUN (entry blocked) | — |
| Artifact promotion | NOT RUN (entry blocked) | — |
| Review boundary | NOT RUN (entry blocked) | partial Q-A evidence only: `reviewRun blocksCompletion=False` ×3 |
| Acceptance | NOT RUN (entry blocked) | partial Q-A evidence only: `workItem.final=Completed` ×3 |
| Workspace integrity | NOT RUN (entry blocked) | partial Q-A evidence only: `missingRequired=[]`, `forbiddenRuntimeState=[]` |
| Credential isolation | NOT RUN (entry blocked) | partial Q-A evidence only: `filesContainingCredential=0`, `logs linesContainingCredential=0` |
| Resource controls | NOT RUN (entry blocked) | baseline only: attempt timeout observed working (Q-A-2 bounded retry) |
| Retry/recovery | NOT RUN (entry blocked) | baseline only: 1 bounded retry, attempts 1→2, budget preserved |
| Idempotency | NOT RUN (entry blocked) | — |
| API authorization | NOT RUN (entry blocked) | — |
| Auditability | **FAIL (baseline)** | B1: `WorkItemExecutionMetadataUnavailable` on all 3 completed live runs |
| End-to-end delivery | LIMITED | Q-A only, 3 of 12 planned runs, no Phase Q gate |
| Regression #1 | **FAIL** | `TestResults/PhaseR/regression-preflight.log` → 1961 / 1915 / 1 F (B2a) / 45 sk |
| Regression #2 | PASS | `TestResults/PhaseR/regression-preflight-2.log` → 1961 / 1916 / 0 F / 45 sk |
| Regression #3 | **FAIL** | `TestResults/PhaseR/regression-preflight-3.log` + `TestResults/PhaseR/Run3/*.trx` (canonical `parse-trx.ps1`) → 1961 / 1915 / 1 F (B2b) / 45 sk, `skipsWithoutGateReason=0` |
| Security audit | PARTIAL | default run made no provider call, no credential read; `.env.live` git-ignored; Docker container audit impossible (daemon down) |
| Docker cleanup | NOT VERIFIABLE | Docker daemon not running; Q evidence shows `factoryOwned=0` after 15 s |
| Final gate | **BLOCKED** | B1, B2, B3/B4 — all Factory-owned defects |

The certification table above is the final element of this report, as Phase R §28 requires. Areas
recorded as `NOT RUN (entry blocked)` were deliberately not exercised: §1 forbids beginning
production-readiness implementation while the entry gate is not `GO`, and §29 forbids recording
unperformed checks as passes.
