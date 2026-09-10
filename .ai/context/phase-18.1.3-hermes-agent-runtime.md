# Phase 18.1.3 — Hermes IAgentRuntime Implementation

## 1. Runtime architecture

```
Factory
  |
  +-- AgentDefinition
  |
  +-- AgentRun
          |
          v
    IAgentRuntime            <-- Application contract (Phase 18.1.2)
          |
          v
  HermesAgentRuntime         <-- Infrastructure adapter (this phase)
          |
          v
    IAgentExecutor            <-- existing Phase 17.x contract, unchanged
          |    (resilience-decorated: retry/circuit-breaker/bulkhead/timeout)
          v
  HermesAgentExecutor         <-- existing, unchanged
          |
          v
    IProcessRunner / SystemProcessRunner   <-- existing, unchanged
          |
          v
       docker run hermes-agent
```

`HermesAgentRuntime` adds **no new process-management code**. It is a thin adapter that maps a canonical `AgentRun` + its `AgentDefinition` into the existing `AgentExecutionRequest`, calls the already-resilience-decorated `IAgentExecutor`, and maps the resulting `AgentExecutionResult` back into the canonical `AgentRunExecutionResult` contract. All process launching, argument construction, environment allow-listing, command-policy enforcement, output capture, timeout handling, and cancellation propagation continue to live exactly where they lived before this phase (`HermesAgentExecutor` / `SystemProcessRunner`).

## 2. IAgentRuntime contract

Unchanged from Phase 18.1.2 — reviewed and found sufficient, not evolved:

```csharp
public interface IAgentRuntime
{
    Task<AgentRunExecutionResult> ExecuteAsync(AgentRun agentRun, CancellationToken cancellationToken = default);
}
```

`AgentRun` already carries everything a Hermes execution needs: `AgentDefinitionId`/`AgentDefinition` (nav, must be loaded by the caller), `CorrelationId`, `Provider`/`Model`, `Input`, and the method's own `CancellationToken`. No Hermes-specific fields were added to the domain model — Hermes-specific detail (docker image, network mode, workspace root, timeout) stays entirely in `HermesOptions` (Infrastructure), as it already did before this phase.

## 3. HermesAgentRuntime

`apps/src/AI.Factory.Infrastructure/Agents/HermesAgentRuntime.cs`. Constructor dependencies: `IAgentExecutor` (the resilience-decorated instance already registered in DI), `IFailureClassifier`, `IArtifactRecorder`, `IArtifactRepository`, `ILogger<HermesAgentRuntime>`.

It does **not** own or mutate `AgentRun.Status` — per the spec's critical architectural rule, that belongs to the future orchestration layer (Phase 18.1.5). It never calls `AgentRun.TransitionTo(...)`. It does **not** create or update `HermesExecution` rows — that legacy record stays owned by the existing `AutonomousCodingOrchestrator`/`WorkItem` pipeline, deliberately not touched by this new runtime, to avoid recreating the exact responsibility-mixing Phase 18.1.2 split apart.

## 4. Distinction between AgentRun and HermesExecution (reaffirmed)

Nothing changed here from Phase 18.1.2. `HermesAgentRuntime` executes an `AgentRun`; it has no dependency on `HermesExecution`/`IHermesExecutionRepository` at all. The two Hermes-execution call paths — the legacy `AutonomousCodingOrchestrator` → `IAgentExecutor` → `HermesExecution` audit trail, and the new `HermesAgentRuntime` → `IAgentExecutor` → (no persistence yet) — currently share only the same underlying `IAgentExecutor`/Docker plumbing, nothing else. This is intentional: Phase 18.1.5 (Agent Orchestration + Worker) is where a real decision gets made about whether/how an `AgentRun`-driven execution also needs a `HermesExecution` audit row for compatibility; this phase does not force that decision.

## 5. Hermes process/session mapping

Investigated: the currently installed Hermes integration has no persistent-session concept at the code level — `HermesAgentExecutor` invokes a fresh, ephemeral `docker run --rm ...` per call, with a brand-new isolated workspace directory (`WorkspacePathGuard.EnsureWithinRoot(WorkspaceRoot, Guid.NewGuid())`) every time. There is no Hermes session ID returned by the current CLI invocation to store. The smallest correct mapping for this phase is therefore: **one `AgentRun` execution → one ephemeral Hermes process invocation**, with `AgentRun.Id`/`CorrelationId` remaining the Factory's own durable identity throughout (nothing derived from or dependent on a Hermes-side identifier). If a future Hermes version exposes a real session ID, it can be added as optional runtime metadata without changing this mapping's shape.

## 6. Configuration

No new configuration was added — `HermesOptions` (`DockerImage`, `DataVolume`, `WorkspaceRoot`, `TimeoutSeconds`, `MaxOutputLength`, `NetworkMode`) and `CommandPolicyOptions` (`AllowedCommands`, `DeniedCommands`) already existed and already satisfy every requirement in spec §17 (strongly typed, validated, environment-variable friendly via the `Hermes`/`CommandPolicy` configuration sections, secret-free by default). `HermesAgentRuntime` does not read configuration directly; it inherits Hermes's configured behavior entirely through the `IAgentExecutor` it wraps. A misconfigured/unavailable Hermes (e.g. `docker` not found) already produces a clear, specific `AgentExecutionResult.Error` ("Hermes could not be started: ...") via the existing `ProcessLaunchException` handling in `HermesAgentExecutor` — `HermesAgentRuntime` passes that message straight through as `AgentRunExecutionResult.ErrorMessage`, never replacing it with a vague "Execution failed."

## 7. Cancellation/timeout behavior

Both layers reused unchanged:

- **Cancellation**: when the caller's `CancellationToken` is cancelled, `SystemProcessRunner` kills the process tree and rethrows `OperationCanceledException`, which propagates untouched through `HermesAgentExecutor` and the resilience decorator up to `HermesAgentRuntime`. `HermesAgentRuntime` is the first place that *catches* it (guarded by `cancellationToken.IsCancellationRequested` so a genuine external cancellation is never confused with something else), converting it into a deterministic `AgentRunExecutionResult(Success: false, FailureCategory: FailureCategory.Cancelled, ...)` — cancellation never throws out of `IAgentRuntime.ExecuteAsync`.
- **Timeout**: `SystemProcessRunner` distinguishes an internal timeout expiry (not caller cancellation) from real cancellation, kills the process, and returns `ProcessRunResult(TimedOut: true)`. `HermesAgentExecutor` turns this into a non-thrown `AgentExecutionResult(Success: false, Error: "Execution timed out after {N} seconds.")`. `HermesAgentRuntime` detects the `"timed out"`/`"timeout"` substring in the error text and classifies it as `FailureCategory.Timeout` (via `IFailureClassifier.Classify(reason, "timeout")`) rather than the generic `FailureCategory.Agent` used for other failures — so `TimedOut` and `Cancelled` are always distinguishable in the result, never collapsed into one generic failure.
- No orphaned processes are possible from anything added in this phase — process cleanup (`process.Kill(entireProcessTree: true)`) is entirely `SystemProcessRunner`'s existing, already-tested responsibility; `HermesAgentRuntime` never touches a `Process` object.

## 8. Output/error handling

Unchanged — `HermesAgentExecutor` already truncates stdout/stderr to `HermesOptions.MaxOutputLength` and captures output via non-blocking `OutputDataReceived`/`ErrorDataReceived` event handlers (no deadlock risk). `HermesAgentRuntime` passes `AgentExecutionResult.Output` straight through to `AgentRunExecutionResult.Output` without further processing, and does not log full prompts/outputs by default (only structured identifiers — see §10).

`ArtifactReferences`: when an `AgentRun` carries both `ProjectId` and `WorkItemId` (not the common case yet, since orchestration doesn't wire these in until Phase 18.1.5) and the Hermes execution produced a workspace, `HermesAgentRuntime` calls the existing `IArtifactRecorder.RecordWorkspaceArtifactsAsync` and returns the resulting `Artifact` ids as `ArtifactReferences` — large file content is never inlined into the result or the `AgentRun` row, satisfying the "reference, don't store raw content" requirement carried over from Phase 18.1.2.

## 9. Security considerations

No new attack surface introduced. `HermesAgentRuntime` never constructs process arguments, never touches environment variables, and never reads secrets — all of that remains inside `SystemProcessRunner` (structured `ArgumentList`, never a concatenated command string) and `HermesAgentExecutor` (explicit `PATH`-only environment allow-list). `HermesAgentRuntime`'s own logging (see §10) emits only `AgentRunId`/`AgentDefinitionId`/`CorrelationId`/duration — never prompts, outputs, or configuration values.

## 10. Observability

Structured `ILogger` messages at the runtime boundary, matching spec §24's event names exactly: `AgentRuntimeStarted`, `AgentRuntimeCompleted`, `AgentRuntimeFailed`, `AgentRuntimeTimedOut`, `AgentRuntimeCancelled`. Each carries `AgentRunId`, `AgentDefinitionId`/`CorrelationId`, and duration where applicable — never full prompts, outputs, API keys, or environment contents. No changes were made to `IOperationalMetrics` — the existing `RecordAgentExecution(bool, long?)` hook (feeding `AgentExecutions`/`AgentFailures`/duration in the operational metrics snapshot) already captures the coarse success/failure signal at the `IAgentExecutor` layer beneath this runtime; adding AgentRun-status-granular metrics (distinguishing Timeout/Cancelled/Failed) was judged out of scope for this phase per spec §24 ("do not redesign the metrics subsystem") and is a natural fit for Phase 18.1.6 (Agent API + Metrics) instead, once there's an API surface to expose them through.

## 11. Testing strategy

- `HermesAgentRuntimeTests.cs` (9 tests): success (output/provider/model/duration preserved), prompt composition from `AgentDefinition.SystemPrompt` + `AgentRun.Input`, non-zero-exit failure (`FailureCategory.Agent`), timeout-text classification (`FailureCategory.Timeout`), cancellation (`FailureCategory.Cancelled`, no exception escapes), disabled-`AgentDefinition` short-circuit (executor never called), missing-`AgentDefinition` contract violation (`ArgumentException`), and artifact-reference recording both with and without `ProjectId`/`WorkItemId`. Uses a hand-rolled `FakeAgentExecutor` (matching the existing `HermesAgentExecutorTests`/`CircuitBreakerTests` convention — no mocking framework) and a real in-memory SQLite `FactoryDbContext` for the artifact-recording paths.
- `FakeAgentRuntime.cs` — a new, standalone, reusable `IAgentRuntime` test double (`Success`/`Failure`/`Timeout`/`Cancellation` modes) for future orchestration/Worker tests (Phase 18.1.5+) to depend on without any Hermes/Docker/network requirement, per spec §19.
- No real Hermes/Docker/network access is required by any test added in this phase — `HermesAgentRuntimeTests` exercises `HermesAgentRuntime` entirely through the `IAgentExecutor` fake, never a real process.
- A dedicated environment-gated `HermesRuntimeSmokeTest` (mirroring the existing `HermesSmokeTest.cs` pattern — early-return no-op unless `AI_FACTORY_HERMES_SMOKE_TEST` is set) was **not** added in this phase: `HermesAgentRuntime` adds no new Hermes-invocation mechanics beyond what `HermesSmokeTest.cs` already exercises through `HermesAgentExecutor` directly, so a second smoke test would duplicate coverage without proving anything new. It was not executed — no real Hermes/Docker access was available or attempted in this session.

## 12. Commands actually executed / results

- `dotnet build apps/AI.Factory.slnx -c Debug` — 0 warnings, 0 errors.
- `dotnet test apps/AI.Factory.slnx -c Debug --no-build --filter "FullyQualifiedName~HermesAgentRuntime"` — 9/9 passed.
- `dotnet test apps/AI.Factory.slnx -c Debug --no-build` (full regression, run once per spec §28) — 740/740 passed (60 Worker.Tests + 460 Application.Tests + 220 Api.Tests).
- `dotnet build apps/AI.Factory.slnx -c Release` — 0 warnings, 0 errors.
- `dotnet ef migrations has-pending-model-changes` — "No changes have been made to the model since the last migration." (confirms no migration was needed or added, per spec §27).
- Architecture grep checks: zero `OpenRouter`/`OpenViking` references in Domain; zero `using AI.Factory.Infrastructure` in Domain or Application; exactly one `: IAgentRuntime` implementation, in Infrastructure.
- **Not executed**: any real Hermes/Docker process, `HermesSmokeTest.cs`, or anything requiring network/external LLM access. No real Hermes execution is claimed to have succeeded.

## 13. Known limitations / what remains for Phase 18.1.4+

- `AgentRunExecutionResult.PromptTokens`/`CompletionTokens`/`TotalTokens` are always `null` from this runtime — Hermes's current CLI invocation (`docker run ... -z <prompt>`) does not surface token-usage metadata, and none was fabricated. A future Hermes CLI/output-format change would need to be reflected here.
- `AgentRunExecutionResult.ToolCallNames` is always empty — no tool system exists yet (Phase 18.1.4).
- Artifact-reference recording only activates when an `AgentRun` carries both `ProjectId` and `WorkItemId` — until Phase 18.1.5 wires these in from a real orchestration flow, this path is exercised only by tests, not production callers.
- No AgentRun-level operational metrics beyond the existing coarse `IAgentExecutor`-level success/failure counters — deferred to Phase 18.1.6.
- No real Hermes smoke test specific to `HermesAgentRuntime` was executed or added (see §11) — the existing `HermesSmokeTest.cs` remains the only real-Hermes-gated test in the repository.
