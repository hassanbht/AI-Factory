# Phase 18.1.5 — Agent Orchestration Service + Worker

## Status: GO

## Architecture

```text
AgentRun
   │
   ▼
IAgentOrchestrator
   │
   ├── IAgentRunRepository   (load, TransitionTo, persist)
   ├── AgentDefinition       (loaded via IAgentRunRepository.GetByIdAsync)
   ├── IAgentToolRegistry    (available tool names — deterministic)
   ├── IAgentMemory          (contextual memory search)
   │
   ▼
IAgentRuntime
   │
   ▼
HermesAgentRuntime            (Phase 18.1.3, unchanged)
   │
   ▼
IAgentExecutor
   │
   ▼
HermesAgentExecutor           (Phase 18.1.3, unchanged) → Hermes process/container
```

`AgentOrchestrator` (`AI.Factory.Application.Agents`) is the one
implementation of `IAgentOrchestrator`. It is not a runtime: it never spawns
processes, never invokes Docker/Hermes, never implements retry/circuit-
breaker logic (all of that already lives beneath `IAgentExecutor`, per
Phase 18.1.3's `ResilienceAgentExecutorDecorator`), and never implements
tool- or memory-provider-specific behavior — it only calls the three
interfaces above.

`AgentRunWorker` (`AI.Factory.Worker`) is the thin execution host that
claims one `AgentRun` at a time and hands it to `IAgentOrchestrator`.

## Execution Flow

```text
1. AgentRunWorker.PollOnceAsync
     → IAgentRunRepository.TryClaimNextPendingAsync()
       (atomic: Queued → Running, under Version optimistic concurrency)
2. AgentOrchestrator.ExecuteAsync(agentRunId)
     → GetByIdAsync (with AgentDefinition included)
     → eligibility check (see Idempotency below)
     → IAgentToolRegistry.GetDescriptors() → available tool names
     → IAgentMemory.SearchAsync(ProjectId, AgentDefinitionId) → contextual memory
     → build a non-persisted execution snapshot (Input augmented with memory, if any)
     → IAgentRuntime.ExecuteAsync(snapshot)
     → map AgentRunExecutionResult → AgentRunStatus, persist via UpdateAsync
3. AgentRunWorker records IOperationalMetrics.RecordAgentExecution(success, durationMs)
```

## AgentRun Lifecycle

Uses the existing, unmodified `AgentRunStatus`/`AgentRunTransitions` state
machine (Phase 18.1.2) — no new enum, no new transition rules:

```text
Created → Queued → Running → Succeeded
                           → Failed
                           → TimedOut
                           → Cancelled
                           → Waiting → Running | Failed | Cancelled
```

`AgentOrchestrator` maps a runtime result to the terminal status via
`FailureCategory`:

```csharp
FailureCategory.Timeout    → AgentRunStatus.TimedOut
FailureCategory.Cancelled  → AgentRunStatus.Cancelled
(anything else on failure) → AgentRunStatus.Failed
```

This works because `HermesAgentRuntime` already classifies every failure
(including a cancelled execution) through `IFailureClassifier` before
returning — the orchestrator trusts that classification rather than
re-deriving cancellation-vs-timeout itself from which `CancellationTokenSource`
fired.

## Worker

`AgentRunWorker : BackgroundService`, structurally mirroring
`OrchestrationRunWorker`'s claim/execute/shutdown pattern (same
`PeriodicTimer` poll loop, same per-poll try/catch failure isolation, same
`IWorkerHealthCoordinator`/`IOperationalMetrics` reporting), deliberately
**without** its lease-renewal machinery — this phase's explicit scope is
"one Worker execution loop, one AgentRun at a time," not distributed/leased
concurrency.

- **Poll**: every `AgentWorkerOptions.PollingInterval` (default 2s), unless
  shutdown has begun.
- **Claim**: `IAgentRunRepository.TryClaimNextPendingAsync()` — oldest
  Queued row, atomic via the existing `Version` concurrency token; returns
  `null` if nothing is queued or another caller won the race.
- **Execute**: hands the claimed run's Id to `IAgentOrchestrator.ExecuteAsync`
  inside a linked `CancellationTokenSource` bounded by
  `AgentWorkerOptions.ExecutionTimeout` and the shutdown token.
- **Failure isolation**: the outer poll loop's `catch (Exception)` means any
  exception from claiming or orchestrating one AgentRun is logged and the
  loop continues on the next tick — one failed AgentRun cannot kill the
  Worker process.
- **Shutdown**: `StopAsync` calls `IApplicationShutdown.TryBeginShutdown()`
  (stops new claims immediately, checked at the top of every poll) then
  waits up to 30s via `IBackgroundOperationTracker.WaitForDrainAsync` for
  any in-flight execution to finish before calling `base.StopAsync`.

## Tool + Memory Integration

- **Tools**: `AgentOrchestrator` calls `IAgentToolRegistry.GetDescriptors()`
  once per execution — the registry already returns only enabled tools,
  sorted by name, so `AgentOrchestrationResult.AvailableToolNames` is
  deterministic. `ReadArtifact` (Phase 18.1.4) is the only tool registered;
  no shell/filesystem-write/browser/Git tool was added, per the phase's
  explicit no-goals. `AgentRunExecutionResult.ToolCallNames` (already on
  the runtime contract) is still always empty — `HermesAgentRuntime` itself
  does not yet call tools; this phase makes the tool set *available* to an
  execution, it does not make Hermes actually invoke them (that requires a
  Hermes-side tool-calling protocol, out of scope here).
- **Memory**: `AgentOrchestrator` calls
  `IAgentMemory.SearchAsync(new AgentMemoryQuery(ProjectId: run.ProjectId,
  AgentDefinitionId: run.AgentDefinitionId))` before invoking the runtime.
  A memory-provider failure is caught and logged, never blocks execution
  (memory is contextual, not required). If any entries come back, they are
  prepended to a **non-persisted** execution-time copy of the `AgentRun`
  (same `Id`/`AgentDefinition`/etc., only `Input` differs) — this keeps
  `IAgentRuntime`'s existing single-argument contract completely unchanged
  while still letting memory reach the prompt `HermesAgentRuntime` builds.
  Nothing is written back to memory after execution in this phase (only
  `SearchAsync` — `StoreAsync` remains available on the interface for a
  future phase to use).

## Idempotency

`AgentOrchestrator.ExecuteAsync` checks the loaded `AgentRun.Status` before
doing anything:

| Status on entry | Behavior |
|---|---|
| `Succeeded`/`Failed`/`TimedOut`/`Cancelled` (terminal) | Skipped — `Executed: false`, no re-execution |
| `Running` | Skipped — `Executed: false`, no second concurrent execution |
| `Created` | Rejected — `Executed: false`, invalid starting state |
| `Queued` / `Waiting` | Transitions to `Running` (persisted under the `Version` concurrency token) then executes; a lost claim race (`ConcurrencyConflictException`) is caught and treated as a skip, not an error |

No distributed lock was introduced — the existing EF Core optimistic
concurrency token (`AgentRun.Version`, already `IsConcurrencyToken()`) is the
entire mechanism, reused for both `TryClaimNextPendingAsync` (Worker-side
claim) and the orchestrator's own Queued/Waiting → Running transition
(direct-invocation path, e.g. from a test or a future API endpoint that
doesn't go through the Worker's claim first).

## Failure Handling / Cancellation

- A thrown, non-cancellation exception from the runtime call is not caught
  by the orchestrator — it propagates to the Worker's outer poll-loop
  isolation. In practice `HermesAgentRuntime` never throws for a normal
  failure; it always returns a classified `AgentRunExecutionResult`.
- `FakeAgentRuntime.Mode.Cancellation` (test double) throws
  `OperationCanceledException` directly to simulate a runtime that does
  throw on cancellation. The orchestrator deliberately does not swallow
  this — the `AgentRun` is left in `Running` (already persisted by the
  earlier claim/transition step) for the Worker or a later investigation to
  handle, rather than the orchestrator guessing at a terminal status it
  cannot justify.

## Dependency Graph (DI)

Registered identically in `Api/Program.cs` and `Worker/Program.cs`:

```csharp
builder.Services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();
```

Worker-only:

```csharp
var agentWorkerOptions = new AgentWorkerOptions();
builder.Configuration.GetSection("AgentWorker").Bind(agentWorkerOptions);
agentWorkerOptions.Validate();
builder.Services.AddSingleton(agentWorkerOptions);
...
builder.Services.AddHostedService<AgentRunWorker>();
```

The API registers `IAgentOrchestrator` (mirroring how it already registers
`IAutonomousCodingOrchestrator`) for a future direct-invocation endpoint,
but does **not** register `AgentRunWorker` — only the Worker process claims
and executes AgentRuns, matching the existing API/Worker split.

`IApplicationShutdown`, `IBackgroundOperationTracker`, `IOperationalMetrics`,
`IFailureClassifier`, `IWorkerHealthCoordinator` were already registered as
singletons in `Worker/Program.cs` before this phase — `AgentRunWorker`
reuses them as-is, no duplicate composition logic was added.

## Testing Strategy

Hand-rolled fakes only, no Moq (existing convention):

- `FakeAgentToolRegistry`, `FakeAgentMemory` (new) — deterministic doubles
  alongside the existing `FakeAgentRuntime` (Phase 18.1.3/18.1.4) and
  `FakeAgentTool` (Phase 18.1.4).
- `AgentOrchestratorTests` — real `AgentRunRepository`/
  `AgentDefinitionRepository` against in-memory SQLite (matching
  `AgentRunRepositoryTests`' established pattern) + `FakeAgentRuntime` for
  the runtime boundary. Covers: valid execution → Succeeded, AgentDefinition
  loaded before runtime invocation, tools resolved through the registry with
  deterministic names, memory resolved and scoped to Project+AgentDefinition,
  runtime failure → Failed, runtime timeout → TimedOut, a throwing
  cancellation propagates (run left Running), invalid starting state
  (`Created`) rejected without invoking the runtime, already-`Succeeded`
  not re-executed, already-`Running` not executed concurrently, unknown
  AgentRunId handled. This is the integration-level test proving the
  complete Factory-owned path (AgentRun → Worker-shaped orchestration call →
  FakeAgentRuntime → AgentRun persisted as Succeeded) without real Hermes.
- `AgentRunRepositoryTests` (extended) — `TryClaimNextPendingAsync`: returns
  null with nothing queued, claims the oldest Queued row and transitions it
  to Running (with AgentDefinition included), a second concurrent call does
  not claim the same row.
- `AgentRunWorkerTests` (new) — claims a queued AgentRun and passes it to
  the orchestrator; idle with nothing queued never calls the orchestrator;
  survives a thrown orchestrator exception and keeps polling; `StopAsync`
  completes cleanly with no unobserved exceptions and a fully drained
  tracker; does not spin aggressively when idle (bounded poll count over a
  fixed window at a known interval).

## Real Hermes

```text
Real Hermes E2E: NOT VERIFIED
```

Per the phase spec, this was deliberately not attempted — all tests use
`FakeAgentRuntime` behind `IAgentRuntime`. No real Hermes process, Docker,
network, OpenRouter, or credentials were used in this phase's tests.

## Known Limitations

- `HermesAgentRuntime` does not actually invoke tools — `ToolCallNames`
  on `AgentRunExecutionResult` remains always empty. The registry's
  descriptors are available to the orchestrator/future callers, but no
  Hermes-side tool-calling protocol exists yet.
- Memory is only searched before execution, never written after — no
  automatic "remember this outcome" flow yet.
- `AgentRun.Iteration`/`Attempt` are not incremented by the orchestrator —
  every execution is currently a single attempt; retry/repair semantics for
  a `Failed`/`TimedOut` run (e.g. creating a new attempt row) are not
  implemented and were not required by this phase's scope.
- No REST API for AgentRuns exists yet — `IAgentOrchestrator` is registered
  in the API host for future use but nothing calls it there.
- One Worker execution loop, one AgentRun at a time — no bounded
  concurrency, no distributed claim beyond the single-process optimistic
  concurrency token described above.

## Migration Status

**NO MIGRATION.** No new persisted fields or tables were required — the
orchestrator reuses `AgentRun`'s existing `Status`, `Output`,
`ErrorMessage`, `FailureCategory`, `PromptTokens`/`CompletionTokens`/
`TotalTokens` columns, and the existing `Version` concurrency token for
claiming. Confirmed no new migration file was generated this phase (the
only pending migration/model-snapshot changes present in the working tree
are leftover uncommitted Phase 18.1.4 work, not anything from this phase).

## Recommended Phase 18.1.6

Agent API + Metrics/Observability — expose the now-working
AgentRun/orchestration capabilities through the API (create/query
AgentRuns, trigger execution) and add AgentRun-status-granular metrics
(queued/started/completed/failed counts, durations) on top of the existing
`IOperationalMetrics`, without redesigning the orchestration core built here.
