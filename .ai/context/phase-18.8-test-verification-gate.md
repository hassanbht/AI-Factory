# Phase 18.8 — Deterministic Test & Verification Gate

## 1. Executive Summary

Closed the gap left explicitly open by Phase 18.7: an agent's self-reported `"completed"` execution status is no longer sufficient proof that a WorkItem's implementation is correct. A `WorkItem` whose agent reports `execution.status: "completed"` now passes through `WorkItemStatus.Testing` and a new deterministic `IVerificationGate` (real `dotnet build` + `dotnet test`, via the existing `IBuildExecutor`/`ITestExecutor`/`IProcessRunner` infrastructure) before it is allowed to reach `Completed`. No new process-execution abstraction, no new AgentRun mechanism, no repair loop, no migration.

## 2. Repository Discovery

Full read-only inspection of `WorkItem`/`WorkItemStatus`/`WorkItemTransitions`, `Project`/`ProjectStatus`/`ProjectTransitions`, `AgentRun`/`AgentRunStatus`/`AgentRunTransitions`, `TaskDispatchScheduler`, `TaskDependencyResolver`, `AgentRunWorker`, `AgentRunWorkItemSync`, `WorkItemExecutionResultMapper`, `StructuredAgentResult`, existing process/build/test infrastructure, API surface, `AGENTS.md`, and the Phase 18.7 report was performed before any code change. Key findings that shaped this design:

- `WorkItemStatus.Testing`/`Repairing`/`Reviewing` already exist in the enum and in `WorkItemTransitions`, but nothing reached them — Phase 18.7 deliberately routed `ExecutionSucceeded` straight to `Completed` to avoid stranding dependents, because `TaskDependencyResolver.Classify` only ever treats `Completed` as satisfying a dependency.
- A full deterministic build/test pipeline already exists: `IBuildExecutor`/`DotNetBuildExecutor`, `ITestExecutor`/`DotNetTestExecutor`, both backed by `IProcessRunner`/`SystemProcessRunner` with a `CommandPolicyOptions` allow/deny list (`dotnet`, `docker` allowed; shells, network tools, destructive commands denied). This is legacy machinery (`ValidationEngine`, `ValidationRun`) not wired into the current WorkItem-execution pipeline, but fully reusable.
- `ExecutionLimitsOptions.BuildTimeoutMinutes`/`TestTimeoutMinutes` already configure bounded timeouts; both are already registered in `AI.Factory.Worker`'s DI graph.
- Recovery/heartbeat (`WorkflowRecoveryHostedService`, Phase 18.4) only ever sweeps `WorkItemStatus.Running` — a `Testing` WorkItem is structurally invisible to it (known limitation, see §16).

## 3. Existing Verification/Process Infrastructure

Reused, unchanged in shape except one additive field each:

- `IBuildExecutor`/`DotNetBuildExecutor`, `ITestExecutor`/`DotNetTestExecutor` — real `dotnet build`/`dotnet test` via `IProcessRunner`.
- `IProcessRunner`/`SystemProcessRunner` — command-policy-enforced process execution, already returns `ProcessRunResult.TimedOut`.
- `ExecutionLimitsOptions` — already-configured build/test timeouts.

Not reused: `IValidationEngine`/`ValidationEngine` — its `ValidationResult` shape discards the `TimedOut` flag needed to deterministically distinguish "timed out" from "could not launch" from "genuine non-zero exit failure," so `VerificationGate` calls `IBuildExecutor`/`ITestExecutor` directly instead (same underlying executors, no new process abstraction).

## 4. Exact Gap Identified

`AgentRunWorkItemSync.DetermineSucceededTarget` mapped `ExecutionSucceeded` directly to `WorkItemStatus.Completed`, trusting the LLM's own claim once its self-report was schema-valid. No independent build/test ever ran.

## 5. Architecture After Phase 18.8

```
AgentRun Succeeded + execution.status="completed"
        |
        v
AgentRunWorkItemSync: Running -> Testing (persisted)
        |
        v
IVerificationGate.VerifyAsync(project, workItem)
        |
   IBuildExecutor.BuildAsync -> IProcessRunner ("dotnet build")
        |  (only if build succeeded)
   ITestExecutor.TestAsync -> IProcessRunner ("dotnet test")
        |
        v
VerificationResult { Passed | Failed | InfrastructureError | TimedOut | Cancelled }
        |
        +--> Passed              -> WorkItem.Completed
        +--> Failed               -> WorkItem.Repairing
        +--> InfrastructureError  -> WorkItem.Failed
        +--> TimedOut             -> WorkItem.Failed
        +--> Cancelled            -> WorkItem.Cancelled
```

Runs synchronously inside the same `AgentRunWorker.PollOnceAsync` call that already invokes `IAgentRunWorkItemSync.SyncAsync` — no second polling loop, no new hosted service, no HTTP-triggered execution.

## 6. Verification Contract

New `IVerificationGate` (`AI.Factory.Application/Abstractions/IVerificationGate.cs`):

```csharp
public interface IVerificationGate
{
    Task<VerificationResult> VerifyAsync(Project project, WorkItem workItem, CancellationToken cancellationToken = default);
}

public enum VerificationStatus { Passed, Failed, InfrastructureError, TimedOut, Cancelled }

public sealed record VerificationResult(VerificationStatus Status, string Summary, int? ExitCode, TimeSpan Duration);
```

Implementation: `VerificationGate` (`AI.Factory.Application/Orchestration/VerificationGate.cs`). Result is ephemeral (per §11 policy) — no persistence beyond the existing `WorkItem.CurrentError` field on non-pass outcomes.

## 7. Trusted Command Resolution

The gate never accepts a command from the LLM or from `WorkItem.Description`. It always runs exactly `dotnet build <Project.RootPath>` then (only if the build succeeded) `dotnet test <Project.RootPath>`, using the already-existing `Project.RootPath` field — the same two executors the (dormant) `ValidationEngine` pipeline already used. `Project.RootPath` is set at Project-creation time by the Factory, never by agent output.

## 8. Security Boundary

No new process-execution surface: `IProcessRunner`'s `CommandPolicyOptions` allow-list (`dotnet`, `docker`) and deny-list (shells, `curl`/`wget`, destructive commands, etc.) already gate every launch, unchanged. No arbitrary command ever reaches `IProcessRunner` from agent output — the gate only ever calls `BuildAsync`/`TestAsync` with `Project.RootPath`.

## 9. WorkItem Lifecycle Policy

Uses the existing `WorkItemTransitions` table unmodified: `Running -> Testing -> {Repairing, Completed, Failed, Cancelled}`. No new transitions were added — all five were already present in `WorkItemTransitions.cs`.

## 10. Dependency Semantics

`ITaskDependencyResolver`/`TaskDependencyResolver` unmodified — it already only treats `Completed` as satisfying a dependency. Proven by:
- `TaskDependencyResolverTests.Resolve_TestingTarget_DependentStaysWaiting` (new, unit-level, unconditional).
- `AgentRunWorkItemSyncTests.SyncAsync_SucceededExecution_DependentWorkItem_NotSatisfiedWhileTesting` (new, uses a gate that blocks until released to observe the WorkItem sitting in `Testing` mid-verification).

## 11. Idempotency / Concurrency

Reuses the exact mechanisms Phase 18.7 already established — no distributed locks, no static in-memory ownership:
- `SyncAsync`'s existing guard (WorkItem must currently be `Running`) still gates entry; a duplicate observation of the same terminal `AgentRun` finds the WorkItem already moved past `Running` and returns.
- Every transition (`Running -> Testing`, then `Testing -> final`) goes through the existing optimistic-concurrency `Version` token; a `ConcurrencyConflictException` on either step is caught and the call returns without retrying — the other writer's outcome wins.
- Proven by `AgentRunWorkItemSyncTests.SyncAsync_AlreadyTerminalWorkItem_NotOverwritten` and `WorkItemExecutionIntegrationTests.DuplicateTerminalResultProcessing_IsSafe` (unchanged, still passing).

## 12. Tests Added

- `VerificationGateTests.cs` (8 facts) — unit-level deterministic mapping: exit 0 -> Passed, non-zero exit -> Failed (build and test), `TimedOut` flag -> TimedOut, `ExitCode==-1 && !TimedOut` -> InfrastructureError, unexpected exception -> InfrastructureError, external cancellation -> Cancelled, bounded summary length.
- `VerificationGateRealBuildIntegrationTests.cs` (2 facts) — real `dotnet build`/`dotnet test` against this repo's own `AI.Factory.Domain.csproj`; missing-project-path -> Failed.
- `TaskDependencyResolverTests.Resolve_TestingTarget_DependentStaysWaiting` (1 fact).
- `AgentRunWorkItemSyncTests.cs` — `SyncAsync_SucceededExecution_RunsVerificationGate_AndAppliesFailurePolicy` (5 theory cases: Passed/Failed/InfrastructureError/TimedOut/Cancelled -> Completed/Repairing/Failed/Failed/Cancelled) and `SyncAsync_SucceededExecution_DependentWorkItem_NotSatisfiedWhileTesting` (1 fact).
- `WorkItemExecutionIntegrationTests.cs` — existing tests updated to inject a fake `IVerificationGate` (Passed) so they continue to exercise scheduler/orchestrator/sync wiring rather than a real build against a seeded Project with no real `RootPath`; all previously-passing assertions (Completed, Failed, duplicate-safety, dependency-readiness) still hold with the gate now genuinely in the loop.

## 13. Exact Verification Results

```
dotnet build AI.Factory.slnx -c Debug    -> Build succeeded. 0 Warnings, 0 Errors.
dotnet build AI.Factory.slnx -c Release  -> Build succeeded. 0 Warnings, 0 Errors.
dotnet test  AI.Factory.slnx -c Debug --no-build:
  AI.Factory.Worker.Tests.dll       : 85 passed, 0 failed, 0 skipped
  AI.Factory.Application.Tests.dll  : 698 passed, 0 failed, 0 skipped
  AI.Factory.Api.Tests.dll          : 283 passed, 0 failed, 0 skipped
dotnet ef migrations has-pending-model-changes --project ../AI.Factory.Infrastructure --startup-project .
  -> "No changes have been made to the model since the last migration."
```

## 14. Real Hermes E2E Result

```
Real WorkItem Verification E2E: NOT RUN
```
Not enabled/executed in this session (`AI_FACTORY_HERMES_SMOKE_TEST` was not set); no such evidence is claimed.

## 15. Migration Status

**No migration.** `VerificationResult` is ephemeral; on a non-pass outcome its bounded summary is written to the existing `WorkItem.CurrentError` field (already used by every other non-Completed transition path since Phase 18.7). `dotnet ef migrations has-pending-model-changes` confirms no schema drift.

## 16. Known Limitations

- **Recovery gap while verifying**: verification runs synchronously inside `AgentRunWorker.PollOnceAsync`, with no heartbeat during that window (the heartbeat loop stops before `SyncAsync` runs, per Phase 18.4 design). If the Worker process crashes mid-verification, the WorkItem is left in `Testing` with no active recovery sweep — `WorkflowRecoveryHostedService` only ever sweeps `Running`. Out of scope per §21 ("do not create another generic recovery framework"); flagged, not fixed.
- **Serial verification**: because it runs inline in the single-AgentRun-at-a-time Worker poll loop, a long-running build/test blocks dispatch of the next queued `AgentRun` for its duration — an existing architectural property of `AgentRunWorker`, not newly introduced, but now exercised on every successful execution report instead of never.
- **`InfrastructureError` vs. genuine build failure at `ExitCode == -1`**: the distinction relies on `ProcessRunResult.TimedOut` plus the convention that `DotNetBuildExecutor`/`DotNetTestExecutor` map a caught `ProcessLaunchException` to `ExitCode = -1, TimedOut = false`. A real `dotnet build`/`dotnet test` process exiting with code `-1` on its own (rare, but not impossible on some platforms) would be misclassified as `InfrastructureError` rather than `Failed`. Accepted as the smallest deterministic rule available without inferring from stdout/stderr text (explicitly forbidden by §15 of the phase spec).

## 17. Remaining Risks

- The verification gate assumes `Project.RootPath` points at something `dotnet build`/`dotnet test` can meaningfully act on; a Project created without a valid `.csproj`/`.sln` at that path will deterministically fail verification (as `Failed`, since a bad path still produces a real non-zero/`-1` exit from `dotnet` itself) rather than silently passing — this is the intended fail-closed behavior, not a defect, but worth the operator's awareness.
- No repair loop consumes the new `Repairing` state yet (explicitly out of scope) — a WorkItem that fails verification currently sits in `Repairing` with nothing to advance it further; that is deliberately left for a future phase.

## 18. Final Decision

WorkItem Test & Verification Gate: VERIFIED
Final Decision: GO
