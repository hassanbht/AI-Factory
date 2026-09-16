# Phase 18.20 — Resilient Agent Result Handling, Real Repair & Live Project Completion

## 0. Executive Summary

This phase had three objectives: make the AgentRun → Verification boundary resilient to incomplete agent reporting without weakening the trust boundary (§3-§9), prove a real Hermes repair end to end (§10), and drive a real Project to completion and human approval (§19-§22).

The first objective is **done and proven**, and it unblocked something that had never happened before in this repository: **a live, real-Hermes WorkItem reached `Completed` through the ordinary production pipeline** — real container, real source workspace, real `dotnet build`/`dotnet test`, real independent review — which is exactly the milestone §18 describes.

Getting there required finding and fixing a **critical production defect** that no deterministic test could see: `AgentCodeReviewer` (and `ModelTaskPlanner`) passed `ProjectId.ToString()` into `AgentExecutionRequest.WorkingDirectory`, which since Phase 18.14 *is* the container's bind-mount path. Against real Hermes every review therefore failed, became a `Critical` finding, blocked completion, and burned the WorkItem's entire repair budget. With a real reviewer in the loop, **no WorkItem could ever have reached `Completed` in production**.

The real repair chain was then driven all the way to a genuine, deterministic verification failure and a real repair dispatch — `Testing → Repairing` on an actual `dotnet test` failure, `SchedulerRepairDispatched … RepairAttemptCount=1` on the real `Repair` AgentDefinition, real Hermes container started. The repair **execution** did not finish: the configured free model exceeded the shipped 5-minute agent execution budget, reproducibly (2 of 2 attempts, cancelled at 300.06s and 300.0s). Per §25 the production timeout was not raised to force a pass, and per §26 this is recorded as a model limitation rather than papered over.

**Final decision: NO-GO**, because §34 is explicit — "real Hermes did not perform the repair" is a NO-GO condition. Everything up to and including real repair *dispatch* is verified; real repair *execution*, Project completion and human approval are not.

---

## 1. Current (pre-phase) structured-result failure behavior

`AgentRunWorkItemSync.DetermineSucceededTarget` treated any unusable report as proof of failure:

| AgentRun | StructuredResult | Old WorkItem target |
|---|---|---|
| Succeeded | valid, `execution.status=completed` | `Testing` |
| Succeeded | valid, `execution.status=repair_required` | `Blocked` |
| Succeeded | missing / `Invalid` | **`Failed`** (terminal, no verification) |
| Succeeded | present but `data.execution` absent/malformed | **`Failed`** (terminal, no verification) |

Phase 18.19 §11 measured the consequence live: the configured coding model performed file edits correctly but omitted `data.execution` on every coding task, so real work was thrown away at a dead end before the Factory ever looked at the workspace.

---

## 2. Selected new interpretation policy

> **Agent completion is a request for Factory verification. Agent output is not proof of correctness.**

| AgentRun | Report | New WorkItem target |
|---|---|---|
| Failed | — | `Failed` |
| TimedOut | — | `Failed` |
| Cancelled | — | `Cancelled` |
| Succeeded | valid `completed` | `Testing` |
| Succeeded | valid `repair_required` | `Blocked` (unchanged existing repair policy — `Running → Repairing` is not a legal transition in `WorkItemTransitions`) |
| Succeeded | **missing / malformed / unrecognized** | **`Testing`** |

`Testing` asserts nothing about correctness. It means "agent execution finished; the Factory must now independently verify the workspace", and `AgentRunWorkItemSync` runs `IVerificationGate` synchronously from there. Absence of metadata is never read as `repair_required` (§4), and no natural-language heuristic was added (§5).

---

## 3. Why the trust boundary remains intact

- `AgentRun.Succeeded` still **never** maps to `WorkItem.Completed`. Locked by a new theory test over all three report shapes (`completed`, malformed, absent) against a gate that fails everything — all three land on `Repairing`.
- The parser (`StructuredAgentResultParser`) and `WorkItemExecutionResultMapper` are **unchanged and still strict**: malformed JSON is still rejected, never re-read as valid (§6). Only the *interpretation* of a rejection moved.
- `Completed` is still produced solely by `IVerificationGate` → `WorkItemCompletionProcessor`.
- No verification was bypassed, no validation weakened, no Docker isolation relaxed.

**Honest limitation of this policy** (observed live, see §7): verification authority is only as strong as the project's own test suite. In the live run, T01's agent did nothing at all, the fixture suite was already green, so verification passed and the WorkItem completed. The Factory cannot detect a no-op agent when nothing in the project tests the requested behavior. This is inherent to "verification decides", not a defect introduced here, and it is the strongest argument for requiring acceptance tests per WorkItem in a future phase.

---

## 4. Exact mapper / synchronization changes

1. **`IWorkItemExecutionResultMapper.cs`** — added `WorkItemExecutionResultOutcome.ExecutionMetadataUnavailable`, explicitly documented as distinct from both `ExecutionRequestedRepair` and runtime failure (§7). `Map` never returns it and still throws.
2. **`AgentRunWorkItemSync.cs`** — extracted `InterpretExecutionReport(agentRun)`, the interpretation boundary: it catches "no structured result / `Invalid` status" and `WorkItemExecutionResultInvalidException` and returns `ExecutionMetadataUnavailable` (logged as `WorkItemExecutionMetadataUnavailable`). `DetermineSucceededTarget` switches on the outcome and routes that case to `Testing` with **no** `CurrentError` — nothing has gone wrong yet, and the gate overwrites it with the real build/test summary if something has.
3. **`HermesOptions.cs` / `HermesAgentExecutor.cs`** — added `DefaultModel`, used as the Hermes `-m` value when an AgentRun carries no model. Nothing in production populated `AgentRun.Model` (neither `TaskDispatchScheduler` nor `ProjectPlanner` sets it), so every scheduler-dispatched run silently used whatever model Hermes had persisted internally, unconfigurable from the Factory. An explicit `AgentRun.Model` still wins.
4. **`AgentCodeReviewer.cs` / `ModelTaskPlanner.cs`** — stopped passing `ProjectId.ToString()` as `AgentExecutionRequest.WorkingDirectory`; both now pass `string.Empty`, the executor's documented "no resolved workspace" path, which provisions a fresh isolated scratch directory. This is also the least-privilege choice: the review prompt already carries requirements, changed files and build/test output, so the reviewer has no reason to be handed the Project's real workspace. **See §9 — this was the critical live-only defect.**

No migration: `dotnet ef migrations has-pending-model-changes` → *"No changes have been made to the model since the last migration."* (§31 satisfied.)

---

## 5. Deterministic test results

New/updated focused tests (§28):

| # | Test | Result |
|---|---|---|
| 1 | `SyncAsync_SucceededWithValidCompletedExecutionResult_TransitionsToCompleted` (via gate) | pass |
| 2 | `SyncAsync_SucceededWithRepairRequiredExecutionResult_TransitionsToBlocked` | pass |
| 3 | `SyncAsync_SucceededWithNoStructuredResult_GoesToVerificationNotFailed` (pins `Testing` mid-flight, asserts no `CurrentError`) | pass |
| 4 | `SyncAsync_SucceededWithMalformedExecutionData_GoesToVerificationNotFailed` | pass |
| 5 | `SyncAsync_SucceededWithoutMetadata_FactoryVerificationDecidesOutcome` (Passed→`Completed`, Failed→`Repairing`) | pass |
| 6 | `SyncAsync_SucceededAgentRun_NeverReachesCompletedWithoutVerification` (theory over all 3 report shapes) | pass |
| 7 | `SyncAsync_SucceededWithoutMetadata_RepeatedSync_IsIdempotent` | pass |
| 8 | `SyncAsync_TerminalRuntimeFailure_TransitionsWorkItem` (Failed→Failed, TimedOut→Failed, Cancelled→Cancelled) | pass (unchanged) |
| 9 | `SyncAsync_AlreadyCancelledWorkItem_NotResurrectedByLateFinishingAgentRun` | pass (unchanged) |
| 10 | `InvalidExecutionResult_IsDecidedByFactoryVerification_NotByTheReport` (rewritten from the old `_ProducesNoUnsafeStateMutation`) | pass |
| 11 | `ReviewAsync_NeverPassesAnIdentifierAsTheWorkingDirectory` (regression lock for §9's defect) | pass |

Full suite after all changes:

```
AI.Factory.Api.Tests          302 passed, 0 failed
AI.Factory.Application.Tests  798 passed, 0 failed
AI.Factory.Worker.Tests        88 passed, 0 failed
Total                        1188 passed, 0 failed, 0 skipped
dotnet build -c Debug        0 warnings, 0 errors
dotnet build -c Release      0 warnings, 0 errors
```

No test was excluded or weakened. Two tests that encoded the superseded dead-end policy were rewritten to the new semantics, as §28 requires.

---

## 6. Live environment

- Docker 29.7.2, daemon running; image `nousresearch/hermes-agent:latest`.
- Container toolchain (verified directly): `git`, `python3` — **no `dotnet`**. The agent therefore cannot run the project's own build/test itself; all build/test evidence comes from the Factory.
- Provider: local OmniRoute gateway at `http://host.docker.internal:20128/v1`; Hermes `--provider openrouter` with `OPENROUTER_BASE_URL` overridden to it; key resolved from the `OMNIROUTE_API_KEY` environment secret. No secrets reproduced here.
- Model: `auto/coding:free` — the same repository-configured router/model used by every real-Hermes test since Phase 18.1.7 (§26: not a model chosen to make this phase pass).
- Timeouts, all left at shipped production values (§25): `HermesOptions.TimeoutSeconds = 300`, `ExecutionLimits.AgentExecutionTimeoutMinutes = 5`, `ExecutionLimits.BuildTimeoutMinutes = 5`. Only the test's own *outer polling windows* were widened (12-15 min per stage).
- Test harness DB (§24): isolated per-test SQLite **file** with `PRAGMA journal_mode=WAL` and `SqliteConnection.ClearAllPools()` on dispose — replacing the shared `:memory:` connection that produced the "database is locked" flake in Phase 18.19. No "database is locked" error occurred in any of the four live runs of this phase. Production database resilience was not touched.

---

## 7. Real Hermes WorkItem result (§18 — first live `Completed`)

Live run 3, Project `e828c125-7a88-4500-848d-4b434bc01318`, workspace `…\live-e2e-cbe3c7444aaf4f20bfb707f8cf8195a2\workspaces\projects\live-repair-demo`:

```
T01 = 0205d65f-6a30-4a62-a1fc-2e6cb4f68208
[08:00:20] Ready -> [08:00:22] Running -> [08:01:02] Testing -> [08:02:12] Completed
```

- `Running -> Testing` in 40s: real container, real model, and the run reached verification. Under the old policy this same run would have dead-ended at `Failed`.
- `Testing -> Completed` in 70s: real `IVerificationGate` (`dotnet build` + `dotnet test`, ~5s) then the real independent review through Hermes.
- Reproduced identically in live run 4: `[08:10:09] Running -> [08:10:49] Testing -> [08:12:00] Completed`.

Recorded honestly: `T01 agent created STATUS.md: False` in both runs. The agent did not perform its instructed edit; the fixture suite was already green, so verification passed and the WorkItem completed. See §3 — this is the policy's inherent ceiling, and it is a real product blocker (§16), not a Factory defect.

---

## 8. Real verification result

Real `dotnet build` + `dotnet test` executed against the Project's own `RootPath` in every cycle:

- T01: `VerificationGateCompleted … Status=Passed DurationMs=5046` (run 3), `Status=Passed` (run 4).
- T02: `Status=Failed` — the deterministic failure this phase's fixture is built around, quoted verbatim from `WorkItem.CurrentError`:

```
Tests failed (exit code 1): Demo.Tests.GreetingServiceTests.GetGreeting_ReturnsExpectedGreeting [FAIL]
```

That test asserts the exact original greeting, so **it can only fail if the agent really changed `src/GreetingService.cs` in the real host workspace** — the entry into `Repairing` is itself independent proof of real workspace modification (§15).

---

## 9. Critical production defect found by the live run

```
Independent review could not be performed for WorkItem …:
Resolved workspace '8784f0ac-3c4a-4085-b1f1-aab944ed75ff' no longer exists or is not accessible.
```

That string is a **ProjectId**, passed where a filesystem path belongs. `AgentExecutionRequest.WorkingDirectory` became the container's authoritative bind-mount path in Phase 18.14, but `AgentCodeReviewer` (and `ModelTaskPlanner`) still passed `ProjectId.ToString()`. Observed consequence chain, end to end, in live run 1:

```
review fails -> Critical/Correctness finding -> BlocksCompletion
  -> WorkItem.Repairing -> repair dispatch -> real Hermes repair run -> verification Passed
  -> review fails again -> Repairing ... x3
  -> "Maximum repair attempts (3) reached" -> WorkItem.Failed
```

Severity: with a real reviewer configured, **no WorkItem could ever reach `Completed`**, regardless of code correctness — while real verification was passing the whole time. Invisible to all 1,186 deterministic tests because every one of them used a fake executor. Fixed in §4.4 and locked by test #11 in §5.

Related, documented but **not** changed: the shipped `ModelRouting.Models` catalog contains placeholder ids (`independent-strong-model-a`, `coding-optimized-model-a`, …) that no real provider can serve, and the review stage routes through it. A real deployment must configure this catalog or the review stage cannot run. The live test configures the existing options object rather than bypassing the router (§26).

---

## 10. Real Repair result

**Repair dispatch: VERIFIED.** **Repair execution: NOT VERIFIED.**

Live run 3, T02 = `6245fa79-59c5-4b3d-98d0-deb3a1023b5e`:

```
[08:02:14] Running -> [08:03:09] Testing -> [08:03:15] Repairing     <- real dotnet test failure
SchedulerRepairDispatched WorkItemId=6245fa79... AgentRunId=548a8419-2f8b-4c9f-ad02-8e64716b6808
                          AgentDefinitionId=9e422b7f-cd82-45bb-89cd-77d649887b4b RepairAttemptCount=1
[08:03:17] Running -> [08:08:17] Cancelled
Operation FAILED: AgentExecution.Attempt ... DurationMs=300061, Reason=Caller cancelled
AgentRunCancelled AgentRunId=548a8419... DurationMs=300060 FailureCategory=Cancelled
```

Live run 4, T02 = `cf507be7-b8c8-4007-ad49-33b3baee88ec`: identical shape, repair run `[08:13:00] -> [08:18:00] Cancelled` — exactly 300s again.

What this does prove (all through the unmodified production pipeline, no status set by hand — §11):
- a real verification failure produced `Repairing`;
- `RepairAttemptCount` advanced 0 → 1 against the existing `ExecutionLimitsOptions.MaxRepairAttempts`, with no new counter (§14);
- the repair was dispatched on the **real `Repair` AgentDefinition** (`9e422b7f…`), a different definition from the implementation agent (`82a7063b…`), through the same AgentRun/Hermes/Docker path, with no special repair executor (§13);
- a real repair container started against the same `Project.RootPath`.

What it does not prove: the repair agent never returned within the production budget, so no repaired source, no second verification pass, and no `Completed` via repair.

**Classification (§27): Repair execution — specific-model latency limitation, not a Factory defect.** Evidence for that classification:
- Same container, model, workspace and Factory for both objective types.
- Implementation objective: 40s / 52s / 55s → completes.
- Repair objective (`BuildRepairObjective`, which embeds the truncated `dotnet test` failure output): >300s, 2/2 runs, cancelled by the caller's own token at the shipped 5-minute `AgentExecutionTimeoutMinutes`.
- The container has no `dotnet`, so the agent cannot be stalled running the project's build/test — the latency is in the model's own execution chain on the larger repair prompt.

Per §25 the production timeout was **not** raised to obtain a pass, and per §26 no Factory correctness rule was weakened to accommodate the model.

---

## 11. Real Project completion result

**NOT VERIFIED.** The Project never left `Implementation`, because T02 never reached `Completed` and `ProjectCompletionGate.TryAdvanceToTestingAsync` correctly requires every WorkItem to be `Completed` first. The gate behaved correctly; it was simply never eligible. `VerifyProjectAsync` (§21) therefore did not run live in this phase.

## 12. Human approval result

**NOT VERIFIED** live — the Project never reached `HumanReview`, so neither the `ConflictException` bypass check nor `ApproveAsync` was exercised against a live Project. Both remain proven deterministically (Phase 18.18, and `GoldenProjectLifecycleE2ETests` still green in this phase's suite run).

---

## 13. Actual identifiers

| Run | Project | WorkItems | Key AgentRuns |
|---|---|---|---|
| Live 1 | `8784f0ac-3c4a-4085-b1f1-aab944ed75ff` | T01 `cacfd5cf-3bf1-42f0-a790-2a6f94250d3c`, T02 `a2cb6a13-3480-4ffc-8424-258b98b0f715` | `ada42e29-dab8-461f-aad4-273c46622327`, `e12d05de-ad83-455a-bc3a-de48086d6df6`, `647c6e5a-e688-4c1e-a776-67c84b21281b` |
| Live 2 | `eb81a43d-e2fb-4fae-9ca8-8330c8b8a6b0` | T01 `20ede6e9-6e72-404b-9f4b-6d0a83524fa7`, T02 `90407a3d-0514-4f86-b766-d373c25880cb` | `8be7f1ff-7981-4ed8-854c-7ac79f9f282e` |
| Live 3 | `e828c125-7a88-4500-848d-4b434bc01318` | T01 `0205d65f-6a30-4a62-a1fc-2e6cb4f68208`, T02 `6245fa79-59c5-4b3d-98d0-deb3a1023b5e` | impl `21067678-750e-410f-ad62-ff82fe89126c`, **repair `548a8419-2f8b-4c9f-ad02-8e64716b6808`** |
| Live 4 | `8b46dbf1-a509-4891-9da0-6169b6dbee92` | T01 `bcfa33de-c904-4679-9b3e-756baa199373`, T02 `cf507be7-b8c8-4007-ad49-33b3baee88ec` | `e85546fc-078c-49a2-a9fb-d72ccf2c6b0d` |

Workspace evidence path (live 3): `…\Temp\live-e2e-cbe3c7444aaf4f20bfb707f8cf8195a2\workspaces\projects\live-repair-demo`, a real bare-clone of the `LiveRepairFixture` Git repo (`Demo.slnx`, `src/GreetingService.cs`, `tests/GreetingServiceTests.cs`).

---

## 14. Manual Golden Path (§23)

**NOT ATTEMPTED.** It is gated on the same repair/completion chain that did not finish; running it would have produced no evidence the automated live runs did not already produce, at significant additional cost.

---

## 15. Remaining model/provider limitations

1. **Repair latency**: `auto/coding:free` exceeds the shipped 5-minute agent execution budget on the repair objective, reproducibly. Either a faster/stronger model or an explicit, justified repair-specific timeout (a deliberate product decision, not a test fix) is required before real repair can complete.
2. **Instruction compliance**: the same model completed a WorkItem without performing the instructed edit (`STATUS.md: False`, 2/2 runs).
3. **Structured output**: unchanged from Phase 18.19 §11 — `data.execution` is omitted on coding tasks. This phase makes that harmless rather than fatal, which was its point.
4. **Model catalog**: shipped `ModelRouting` ids are placeholders; a real deployment must configure them or the review stage cannot run.

## 16. Remaining product blockers

1. **Verification can only catch what the tests cover.** A no-op agent on an already-green project completes its WorkItem. WorkItem-level acceptance criteria that the Factory turns into executable checks are the missing piece; until then "Completed" means "did not break the existing suite", not "did the work".
2. Real repair execution, live Project completion, and live human approval remain unproven (§10-§12).

---

## 17. GO / NO-GO

```text
DETERMINISTIC TESTS:          PASSED   (1188 passed, 0 failed)
REAL HERMES CORE E2E:         PASSED   (real WorkItem reached Completed, twice)
REAL HERMES REPAIR E2E:       NOT VERIFIED   (dispatch proven; execution timed out 2/2)
REAL PROJECT COMPLETION E2E:  NOT VERIFIED   (never eligible — gate behaved correctly)
MANUAL GOLDEN PATH:           NOT VERIFIED   (not attempted)

Resilient Agent Results & Live Completion: NOT VERIFIED
Final Decision: NO-GO
```

Measured against §33's GO list: result handling is fully met (missing metadata no longer dead-ends a successful run; malformed metadata is never treated as success; no agent output can produce `Completed`); real execution is fully met; **real repair is not** (no completed repair execution); **project completion is not**. Safety is met: Docker isolation untouched, no test-only lifecycle bypass, deterministic suite green, production timeouts unchanged.

The phase nevertheless moved the product materially: the dead-end that made live completion impossible is gone, a critical review-path defect that would have blocked *every* production WorkItem is fixed, an unconfigurable model path is fixed, and the live repair chain is now proven correct right up to the model's own latency wall — which is now the single, precisely-classified blocker.
