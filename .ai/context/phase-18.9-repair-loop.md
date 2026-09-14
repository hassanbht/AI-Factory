# Phase 18.9 — Deterministic Repair Loop & Re-Execution

## 1. Executive Summary

Closed the lifecycle gap left open by Phase 18.8: `WorkItemStatus.Repairing` previously had no execution path. This phase adds a bounded, deterministic repair loop that reuses the existing `TaskDispatchScheduler`, `AgentOrchestrator`/`AgentRuntime`/Hermes pipeline, and `IVerificationGate` unmodified in behavior — a `Repairing` WorkItem is now claimed by the scheduler, dispatched to a dedicated `Repair`-type `AgentDefinition` through the existing AgentRun machinery, and routed back through `Running -> Testing -> IVerificationGate` exactly like a normal implementation attempt. No new runtime, no new scheduler, no new verification engine, no repair-agent bypass of verification, and no migration beyond one additive counter column.

## 2. Repository Discovery

Full read-only inspection (this session, before any code change) covered: `AgentDefinition` (free-text `AgentType`, `Enabled` flag, no seeding mechanism anywhere in the repo), `IAgentDefinitionRepository` (no lookup-by-type method — filtering happens client-side), `TaskDispatchScheduler` (full 271-line file), `TaskSchedulerOptions`, `AgentRunRepository`/`TryClaimNextPendingAsync`, `WorkItem.AttemptCount`/`MaxAttempts` (confirmed **dead** in the new AgentRun pipeline — only read/written by the legacy `AutonomousCodingOrchestrator`/`TaskStateManager` path), `ExecutionLimitsOptions.MaxRepairAttempts` (already existed, default 3, previously read only by the legacy `TaskStateManager`), `AgentOrchestrator` (confirmed agent-type-agnostic — `AgentDefinition.AgentType` is never branched on at execution time, only at the scheduler's lookup stage), `WorkItemTransitions` (`Repairing` allowed only `{Testing, Failed, Cancelled}` — no `Running` target), and the existing test conventions in `TaskDispatchSchedulerTests.cs`/`WorkItemExecutionIntegrationTests.cs`/`AgentRunWorkItemSyncTests.cs`.

## 3. Existing Infrastructure Reused

- `TaskDispatchScheduler` — extended in place (no second scheduler) to also claim `Repairing` WorkItems each cycle.
- `AgentRunRepository.CreateAsync`/`TryClaimNextPendingAsync`, `AgentOrchestrator`, `IAgentRuntime`/Hermes — a repair `AgentRun` is a completely ordinary `AgentRun`; nothing about its execution path differs from an implementation `AgentRun`.
- `AgentRunWorkItemSync` — **entirely unmodified**. Its existing `ExecutionSucceeded -> WorkItemStatus.Testing` mapping (Phase 18.8) already runs `IVerificationGate` regardless of which `AgentDefinition.AgentType` produced the AgentRun, and its existing runtime-failure mapping (`Failed`/`TimedOut -> WorkItem.Failed`, `Cancelled -> WorkItem.Cancelled`) already gives a repair AgentRun's own runtime failure a deterministic terminal outcome. Reusing this unmodified is itself the chosen Phase 18.9 §14 policy — see §8 below.
- `IVerificationGate`/`VerificationGate` (Phase 18.8) — unmodified; still the only path to `Completed`.
- `WorkItem.CurrentError`, `Version` (optimistic concurrency), `AgentRunWorker`'s existing WorkItem-heartbeat loop (keyed only off `AgentRun.WorkItemId`, not `AgentType`) — all reused as-is.
- `ExecutionLimitsOptions.MaxRepairAttempts` (already existed, default 3) — reused as the repair budget; no new configuration section.

## 4. Exact Gap Before Phase 18.9

`WorkItemTransitions` allowed `Repairing -> {Testing, Failed, Cancelled}` but nothing in the running system ever transitioned a WorkItem *out of* `Repairing` toward re-execution — no scheduler branch recognized it, no repair `AgentRun` was ever created, and `Repairing -> Running` was not even a legal transition.

## 5. Architecture After Phase 18.9

```
Testing --(VerificationGate Failed)--> Repairing
                                            |
                                   TaskDispatchScheduler
                              (repair candidates, dispatched first,
                               same concurrency budget as Ready)
                                            |
                              RepairAttemptCount < MaxRepairAttempts?
                                    /                        \
                                  no                         yes
                                   |                           |
                             WorkItem.Failed          AgentDefinition("Repair")
                        (budget exhausted,             enabled & registered?
                         CurrentError set)               /            \
                                                         no             yes
                                                          |               |
                                              WorkItem stays        RepairAttemptCount++
                                              Repairing              Repairing -> Running
                                              (fail-closed,          (atomic, Version-guarded)
                                               retried next               |
                                               cycle once an          Repair AgentRun created
                                               operator adds               |
                                               the definition)     AgentRunWorker -> AgentOrchestrator
                                                                    -> IAgentRuntime (Hermes)
                                                                            |
                                                              AgentRunWorkItemSync (UNMODIFIED)
                                                                    /            \
                                                            Succeeded      Failed/TimedOut/Cancelled
                                                                |                    |
                                                        Running -> Testing    WorkItem.Failed / Cancelled
                                                                |
                                                        IVerificationGate (UNMODIFIED)
                                                          /         \
                                                      Passed       Failed
                                                        |             |
                                                  WorkItem       WorkItem.Repairing
                                                  .Completed     (loop repeats, bounded
                                                                  by MaxRepairAttempts)
```

## 6. Repair Trigger Policy

A repair AgentRun is only ever created for a WorkItem whose `Status == Repairing`. `Repairing` has exactly one entry edge in `WorkItemTransitions` (`Testing -> Repairing`), reached only by `AgentRunWorkItemSync` on a `VerificationStatus.Failed` result — so being `Repairing` is itself the proof that "the previous verification result indicates a repairable implementation failure" (Phase 18.9 §4); no additional flag or check was needed. `InfrastructureError`/`TimedOut`/`Cancelled` verification outcomes were left mapped to `WorkItem.Failed`/`Cancelled` in Phase 18.8 (unchanged here) and therefore never reach `Repairing` at all — they are structurally excluded from the repair loop, exactly per §4's "do not reinterpret infrastructure failures as implementation failures."

## 7. Repair AgentRun Design

- **AgentType convention**: `TaskDispatchScheduler.RepairAgentType = "Repair"` (a new public const), looked up the same way as the implementation convention (`Enabled && AgentType == "Repair"`) but independently of `WorkItem.Type` — a repair agent is never conflated with the implementation agent that produced the failing code. No `AgentDefinition` seeding was added (none exists for `Implementation` either — an established gap, not something this phase should invent a seeding story for; per §5, "do not automatically seed an AgentDefinition unless the repository already has an established seeding mechanism").
- **Linkage**: `AgentRun.ProjectId = WorkItem.ProjectId`, `AgentRun.WorkItemId = WorkItem.Id` — identical to the existing `DispatchOneAsync` construction.
- **Distinguishability**: via `AgentRun.AgentDefinitionId -> AgentDefinition.AgentType == "Repair"` — no new `AgentRun`/`AgentDefinition` field was added; the existing `AgentType` string already represents this without ambiguity.
- **Objective**: `TaskDispatchScheduler.BuildRepairObjective` — same structured-JSON `execution.status` contract as the implementation objective, plus the WorkItem's identity/title/description, the current `RepairAttemptCount`, and a defensively-truncated (2000 char) copy of `WorkItem.CurrentError` (the verification failure). It explicitly instructs the agent not to expand scope, never mentions or lets the agent supply a verification command, and states that success will be independently re-verified — the LLM cannot influence which command is run and cannot make its own "completed" report sufficient (enforced by code, not merely by the prompt — see §10).

## 8. Attempt / Retry Policy

- **Counter**: new `WorkItem.RepairAttemptCount` (int, default 0) — a dedicated field rather than reusing `AttemptCount`/`MaxAttempts`, because those already have an established, different owner (the legacy `TaskStateManager`/`AutonomousCodingOrchestrator` pipeline) with a different exhaustion policy; reusing them would have silently conflated two independent attempt-counting semantics over the same column for any WorkItem that could theoretically be touched by both pipelines. This satisfies §11's "if a new field is genuinely required, first prove existing fields cannot represent the state."
- **Budget**: reused `ExecutionLimitsOptions.MaxRepairAttempts` (already existed, default 3, already validated at startup) — no new configuration was introduced.
- **Policy**: `RepairAttemptCount < MaxRepairAttempts` -> dispatch (increment then claim); `>= MaxRepairAttempts` -> `Repairing -> Failed`, with `CurrentError` set to a bounded message containing "Maximum repair attempts reached" and the latest verification failure summary. No further agent is invoked once exhausted.
- **Repair AgentRun runtime failure** (Phase 18.9 §14, "Prefer" language, not "must"): the chosen policy is that a repair AgentRun ending `Failed`/`TimedOut`/`Cancelled` at the *runtime* level (not a verification failure) maps to a terminal `WorkItem.Failed`/`Cancelled` via the existing, **unmodified** `AgentRunWorkItemSync.DetermineTarget` — the same mapping every other AgentRun runtime failure already uses. An automatic "retry repair on runtime failure" was deliberately not implemented: it would require a new `Running -> Repairing` transition (a further lifecycle change beyond the one §10 explicitly authorizes) and would couple the generic, type-agnostic `AgentRunWorkItemSync` to repair-specific semantics, for a requirement the spec itself only phrases as "Prefer," not "must." This is a conservative, fail-closed choice, documented and tested (`RepairAgentFailure_UsesDeterministicPolicy`, `RepairAgentCancellation_CancelsWorkItem`).

## 9. Concurrency / Idempotency

No new locking of any kind. The repair claim (`RepairAttemptCount += 1` and `Repairing -> Running`) is written in a single `IWorkItemRepository.UpdateAsync` call guarded by the existing optimistic-concurrency `Version` token — identical mechanism to `DispatchOneAsync`'s normal claim. A `ConcurrencyConflictException` is caught and the losing scheduler cycle returns a `ConcurrentClaimLost` outcome without retrying or overwriting. Proven by `ConcurrentRepairDispatch_CreatesOnlyOneRepairAgentRun` (two schedulers racing via `Task.WhenAll`), `RepairClaim_UsesOptimisticConcurrency` (a stale in-memory copy's write is rejected), `RepairClaim_IsAtomic` (attempt count and status transition always change together, never independently), and `DuplicateRepairObservation_IsSafe`.

## 10. Verification Integration

`WorkItemTransitions.Repairing` gained exactly one new allowed target: `Running` (the smallest domain change needed, per §10's explicit instruction). No `Repairing -> Completed` transition was added or is reachable — `Completed` is only ever reached via `Testing -> Completed`, itself only reachable after a real `IVerificationGate.Passed` result. `AgentRunWorkItemSync` is completely unmodified, so the exact same "agent's completed self-report only earns Testing, never Completed directly" guarantee from Phase 18.8 applies identically to repair AgentRuns — proven directly by `RepairAgentCannotDirectlyCompleteWorkItem` and `VerificationCommands_AreStillFactoryOwned` (a repair AgentRun's structured result was made to contain a smuggled fake "command" in its summary/notes text; the test proves `IVerificationGate.VerifyAsync` is still invoked with only `(Project, WorkItem)` and produces the same outcome regardless).

## 11. Dependency Semantics

`ITaskDependencyResolver`/`TaskDependencyResolver` — **completely unmodified**. It already only ever treats `Completed` as satisfying a dependency (`target.Status != Completed -> allCompleted = false`), so `Repairing` (and `Testing`, `Running`, `Ready`) were already excluded by construction. Proven by the new unit test `TaskDependencyResolverTests.Resolve_RepairingTarget_DependentStaysWaiting` (unconditional) and the integration test `CompletedAfterRepair_SatisfiesDependentWorkItem` (a dependent only becomes dispatchable after its repaired dependency reaches `Completed`).

## 12. Recovery / Heartbeat Behavior

No changes to `WorkflowRecoveryHostedService`, `ITaskStateManager`, or `AgentRunWorker`'s heartbeat loop. A repair `AgentRun` has `WorkItemId` set exactly like an implementation `AgentRun`, and `AgentRunWorker.RunHeartbeatLoopAsync` is keyed purely off `AgentRun.WorkItemId` — it already treats a repair execution identically, with zero code change required (confirmed by inspection, per §16's own hint that this should already be covered automatically). The Phase 18.8 known limitation (a WorkItem sitting in `Testing` during synchronous verification has no active recovery sweep, since `WorkflowRecoveryHostedService` only ever sweeps `Running`) is unchanged and equally applies whether the AgentRun that produced the `Testing` transition was an implementation or repair run — no new recovery framework was introduced, per the explicit non-goal in §16/§22.

## 13. Tests Added

**`RepairDispatchTests.cs`** (12 facts, scheduler-level, real SQLite): `RepairingWorkItem_CreatesRepairAgentRun`, `RepairingWorkItem_UsesRepairAgentDefinition`, `RepairingWorkItem_LinksProjectAndWorkItem`, `RepairingWorkItem_TransitionsToRunning`, `ConcurrentRepairDispatch_CreatesOnlyOneRepairAgentRun`, `DuplicateRepairObservation_IsSafe`, `RepairClaim_UsesOptimisticConcurrency`, `MaxRepairAttempts_FailsWorkItem`, `RepairAttemptCount_IsPersisted`, `RepairClaim_IsAtomic`, `MissingRepairAgentDefinition_FailsClosed`, `RepairInput_IsBounded`.

**`RepairVerificationLoopTests.cs`** (10 facts, full pipeline: scheduler + `AgentOrchestrator`(`FakeAgentRuntime`) + `AgentRunWorkItemSync` + fake `IVerificationGate`): `RepairAgentSucceeded_ReturnsToTesting` (a blocking gate proves the transient `Testing` state), `RepairAgentCannotDirectlyCompleteWorkItem`, `VerificationPassedAfterRepair_CompletesWorkItem`, `VerificationFailedAfterRepair_ReentersRepairing`, `RepairAgentFailure_UsesDeterministicPolicy`, `RepairAgentCancellation_CancelsWorkItem`, `CompletedAfterRepair_SatisfiesDependentWorkItem`, `VerificationCommands_AreStillFactoryOwned`, and the required **end-to-end integration test** `EndToEnd_ImplementationFailsVerification_RepairsSuccessfully_CompletesWorkItem` (Project -> WorkItem -> initial implementation AgentRun -> verification fails -> `Repairing` -> repair AgentRun -> `Testing` -> verification passes -> `Completed`, asserting the correct `AgentDefinition` was used for the repair run, `RepairAttemptCount == 1`, and exactly 2 `AgentRun` rows total).

**`TaskDependencyResolverTests.cs`**: `Resolve_RepairingTarget_DependentStaysWaiting` (new).

Existing `TaskDispatchScheduler` constructor call sites (`TaskDispatchSchedulerTests.cs`, `WorkItemExecutionIntegrationTests.cs`, `ProjectPlanSchedulerIntegrationTests.cs`, `ProjectPlannerServiceTests.cs`) were updated to pass the new `ExecutionLimitsOptions` dependency — no behavioral changes to those existing tests; all still pass unmodified otherwise.

## 14. Exact Build/Test Results

Targeted repair tests first, run separately:
```
dotnet test tests/AI.Factory.Application.Tests --filter "RepairDispatchTests|RepairVerificationLoopTests|TaskDependencyResolverTests"
  -> Bestanden: 30 erfolgreich, 0 Fehler
```

Full suite:
```
dotnet build AI.Factory.slnx -c Debug    -> Build succeeded. 0 Warnings, 0 Errors.
dotnet build AI.Factory.slnx -c Release  -> Build succeeded. 0 Warnings, 0 Errors.
dotnet test  AI.Factory.slnx -c Debug --no-build:
  AI.Factory.Worker.Tests.dll       : 85 passed, 0 failed, 0 skipped
  AI.Factory.Application.Tests.dll  : 720 passed, 0 failed, 0 skipped
  AI.Factory.Api.Tests.dll          : 283 passed, 0 failed, 0 skipped
```

## 15. Migration Status

**One additive migration**: `20260911114814_AddWorkItemRepairAttemptCount` — adds `work_items.RepairAttemptCount INTEGER NOT NULL DEFAULT 0`. Required because no existing counter safely represented repair-specific attempts without conflating with the legacy `AttemptCount`/`MaxAttempts` fields (see §8). `dotnet ef migrations has-pending-model-changes` confirms no further drift after the migration.

## 16. Real Hermes E2E Result

```
Real Repair Hermes E2E: NOT RUN
```
`AI_FACTORY_HERMES_SMOKE_TEST` was not set in this session; no such evidence is claimed. The existing Phase 18.7/18.8 opt-in mechanism would cover a repair AgentRun without modification, since a repair AgentRun uses the exact same `AgentOrchestrator`/`IAgentRuntime`/Hermes path as an implementation AgentRun — only the `AgentDefinition.AgentType`/`SystemPrompt` and `AgentRun.Input` differ.

## 17. Known Limitations

- **No auto-retry on repair-runtime-failure**: a repair AgentRun's own runtime failure (Hermes crash, timeout, cancellation) goes straight to terminal `WorkItem.Failed`/`Cancelled` rather than re-entering `Repairing` for another attempt, even though `RepairAttemptCount` budget might remain. This is a deliberate, documented choice (§8) favoring the smaller, lower-risk change over the "Prefer" (not "must") language in the phase spec.
- **Recovery gap during `Testing` is unchanged** (inherited from Phase 18.8): a Worker crash mid-verification (whether following an implementation or repair AgentRun) leaves the WorkItem in `Testing` with no active recovery sweep, since `WorkflowRecoveryHostedService` only sweeps `Running`.
- **No `AgentDefinition` seeding**: an operator must register an enabled `AgentDefinition` with `AgentType == "Repair"` before any repair dispatch can occur — exactly the same manual-registration requirement that already exists for `Implementation`/any other `WorkItem.Type`. `MissingRepairAgentDefinition_FailsClosed` proves the scheduler leaves the WorkItem safely in `Repairing` (untouched, attempt count unchanged) rather than failing or guessing, so a later registration lets the very next cycle recover automatically.
- **Repair candidates are prioritized over Ready candidates** within the shared concurrency budget each cycle (§17's suggested ordering policy). Under sustained heavy repair volume this could delay Ready dispatch more than a different ordering would, though it cannot starve Ready work indefinitely — capacity is recomputed fresh every cycle, and repair candidates are inherently bounded (`MaxRepairAttempts`-limited, terminal on exhaustion).

## 18. Remaining Risks

- If an operator ever points a repair `AgentDefinition`'s `SystemPrompt`/tooling at something that can bypass the trusted `IProcessRunner` command-policy allow-list (a pre-existing, out-of-scope risk unrelated to this phase — the allow-list itself was not touched), the same risk that already existed for the implementation pipeline would apply equally here. No new attack surface was introduced by this phase specifically.
- `RepairAttemptCount` is a per-WorkItem counter with no time-based decay — a WorkItem repeatedly failing and being manually reset (e.g. via a future admin action) outside the normal `WorkItemTransitions` guard could reset the budget; no such reset path exists today, so this is theoretical, not an active gap.

## 19. Final Decision

Repair Loop & Re-Execution: VERIFIED
Final Decision: GO
