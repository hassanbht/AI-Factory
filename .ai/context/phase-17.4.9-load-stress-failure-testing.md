# Phase 17.4.9 — Load, Stress & Failure Testing

Status: living document. Sections 2–7 summarize work produced across three
parallel work-streams (Application resilience/stress, API load/database,
Worker restart/recovery). If a section below still says "not yet observed",
re-check the referenced path — it may have landed after this file was last
updated.

## 1. Testing Architecture

No new test project was created. Phase 17.4.9 coverage was added to the
three existing test projects, separated from the fast default suite purely
by xUnit `[Trait("Category", "...")]` attributes — matching the "if a
separate project is unnecessary, use explicit test categories/traits" option
in the spec (section 2).

Categories in use:

| Category | Meaning | Example location |
|---|---|---|
| `Resilience` | Worker lifecycle / interruption / recovery | `AI.Factory.Worker.Tests\WorkerRestartRecoveryTests.cs` |
| `Stress` | Repeated/concurrent resilience-mechanism exercising (circuit breaker, bulkhead, retry/timeout, leak detection) | `AI.Factory.Application.Tests\Stress\*` |
| `Load` | Concurrent HTTP load against the real API test host | `AI.Factory.Api.Tests\Load\*` |
| `FailureInjection` | Reusable deterministic dependency-failure simulation used by `Stress` tests | `AI.Factory.Application.Tests\Stress\FailureInjection\DependencyFailureSimulator.cs` (infrastructure, not itself a test category) |
| `Container` | Manual Docker Compose scenario validation (not automated xunit) | see section 9 |

`AI.Factory.Api.Tests\DatabaseConcurrencyTests.cs` currently carries **no**
Category trait — it runs as part of the default/untagged suite (see section
7 and the note in section 11).

## 2. Test Categories and How to Run Each

```powershell
# Everything (default suite — see section 11 for what "default" includes)
dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s

# Just the new Worker interruption/restart/fault tests
dotnet test apps/AI.Factory.slnx --filter "Category=Resilience"

# Circuit breaker / bulkhead / retry-timeout / leak-detection stress tests
dotnet test apps/AI.Factory.slnx --filter "Category=Stress"

# Concurrent API load, rate-limit burst, correlation isolation
dotnet test apps/AI.Factory.slnx --filter "Category=Load"
```

`FailureInjection` is not a standalone runnable category today —
`DependencyFailureSimulator` and `FailureMode` are test infrastructure
consumed by `Stress`-tagged tests, not a separately tagged test class.

## 3. Load Scenarios (spec section 4)

Implemented under `apps/tests/AI.Factory.Api.Tests/Load/`:

- **Sustained read load** — `ConcurrentApiLoadTests.cs`: concurrent GET
  requests against representative endpoints; asserts no unexpected 500s and
  bounded latency.
- **Concurrent write load** — covered by `ConcurrentApiLoadTests.cs` and
  `DatabaseConcurrencyTests.cs` (competing writes producing HTTP 409 via
  `ConcurrencyConflictException`, no duplicate persistence).
- **Burst load / rate limiting** — `RateLimitBurstStressTests.cs`: burst
  significantly above the configured limit, asserts HTTP 429 with
  `Retry-After` and that health endpoints stay reachable.
- **Mixed workload / correlation isolation** — `CorrelationIsolationLoadTests.cs`:
  concurrent requests verifying correlation IDs never leak across requests.

## 4. Failure Injection Strategy

`apps/tests/AI.Factory.Application.Tests/Stress/FailureInjection/DependencyFailureSimulator.cs`
provides:

- `FailureMode` enum: `Success`, `TransientFailure`, `PermanentFailure`,
  `Timeout`, `Cancellation`, `DelayedSuccess`, `DelayedFailure`.
- `FailureInjectionScenario(FailureMode Mode, TimeSpan? Delay)` — one
  scripted step.
- `DependencyFailureSimulator(params FailureInjectionScenario[] script)` —
  plays back the script one step per call to `ExecuteAsync<T>`; once
  exhausted, the last step repeats. `Always(mode, delay)` is a one-line
  helper for "every call behaves the same way."
- `SimulatedTransientFailureException` (an `IOException`, so it is
  classified transient by `FailureClassifier`) and
  `SimulatedPermanentFailureException` for the two failure exception types.

This is test-only infrastructure living under `AI.Factory.Application.Tests`
— it is never referenced from `apps/src/**`, so it cannot leak into
production execution paths.

## 5. Circuit Breaker Stress Behavior

`apps/tests/AI.Factory.Application.Tests/Stress/CircuitBreakerStressTests.cs`
covers the full lifecycle from spec section 6:

- `ClosedToOpen_ReachesThreshold_ThenFailsFast` — Closed → Open on
  consecutive failures, subsequent calls fail fast without invoking the
  dependency.
- `OpenToHalfOpen_AllowsOnlyConfiguredProbeCount_UnderConcurrentPressure` —
  Open → HalfOpen, only the configured probe count executes concurrently.
- `HalfOpenToClosed_ProbeSucceeds_ResetsFailureCount` — HalfOpen → Closed on
  a successful probe.
- `HalfOpenToOpen_ProbeFails_ReopensImmediately_AndRestartsBreakDuration` —
  HalfOpen → Open on a failing probe, break duration restarts.
- `RepeatedOpenCloseCycles_NoStateLeak_AcrossManyIterations` — stability
  under repeated cycles (section 13).

## 6. Bulkhead Saturation Behavior

`apps/tests/AI.Factory.Application.Tests/Stress/BulkheadStressTests.cs`
covers permit-release correctness (spec section 7):

- `PermitRelease_AfterExceptionInsideUsing_ReturnsToZeroAndAllowsReacquire`
- `PermitRelease_AfterHeldOperationTimesOut_ReturnsToZero`
- `PermitRelease_AfterCancellationWhilePermitAlreadyHeld_ReturnsToZero`
- `RepeatedMixedCycles_UnderConcurrentLoad_NoPermitOrQueueLeak` — the
  repeated stress scenario the spec calls for to detect semaphore leaks.
- `ConcurrentQueueOverflow_RejectsExactlyExpectedCount` — queue capacity
  enforcement under concurrent pressure.

Retry/timeout stress (`RetryTimeoutStressTests.cs`, spec section 8) and
general resource-leak stability (`ResourceLeakStressTests.cs`, spec section
13) live alongside these in the same `Stress` folder.

## 7. Database Concurrency Tests

`apps/tests/AI.Factory.Api.Tests/DatabaseConcurrencyTests.cs`:

- `ConcurrentUpdate_OnProject_ThrowsConcurrencyConflictException_AndPreventsLostUpdate`
- `ConcurrentUpdate_OnWorkItem_ThrowsConcurrencyConflictException`
- `ConcurrentUpdate_OnApprovalRequest_ThrowsConcurrencyConflictException`

These exercise the lost-update scenario from spec section 9.1 (optimistic
concurrency, HTTP 409 mapping, entity version increment) across the three
primary aggregates. This file carries no `Category` trait, so it currently
runs as part of the default `dotnet test` suite (see section 11).

SQLite busy/locked simulation (9.3) and idempotency-under-concurrency (9.4):
not observed under this project as of this writing — check
`apps/tests/AI.Factory.Api.Tests/` and `apps/tests/AI.Factory.Application.Tests/`
for additional files if this section still says so at review time.

## 8. Worker Interruption Tests (this work-stream)

`apps/tests/AI.Factory.Worker.Tests/WorkerRestartRecoveryTests.cs`
(`[Trait("Category", "Resilience")]`), extending the existing
`OrchestrationRunWorkerShutdownTests.cs` pattern:

### 10.1 Graceful cancellation
- `StopAsync_RejectsClaimAttemptedMidDrain_AndReachesIsStoppingBeforeDrainCompletes` —
  confirms `IApplicationShutdown.IsStopping` becomes true before the drain
  completes, and that a claim attempted mid-drain returns nothing (the fake
  repository never returns work once shutdown has begun — matching
  `OrchestrationRunWorker.PollOnceAsync`'s `_shutdown.IsStopping` short-circuit).
- `Worker_StopsClaimingNewWork_AsSoonAsStopBegins` — claim count stops
  increasing once `StopAsync` is called, even though the poll loop's own
  `PeriodicTimer` ticks continue to exist in the background.

### 10.2 Worker restart — no duplicate execution
- `SecondWorker_RecoversStaleLease_ExactlyOnce_AfterFirstWorkerIsInterruptedMidRun` —
  simulates a shared-database fake (`RestartSimulationRepository`) across two
  `OrchestrationRunWorker` instances representing before/after a hard
  process restart. Verifies:
  1. First instance claims the run (`Pending` → `Running`).
  2. A hard interruption is simulated (lease left expired, no clean release).
  3. Recovery goes through the **real production entry point** —
     `IOrchestrationRunRepository.RecoverStaleLeasesAsync`, which is what
     `WorkflowRecoveryHostedService.RunStaleSweepAsync` calls, not the
     worker itself. This matches actual production wiring: recovery is a
     separate hosted service, not part of `OrchestrationRunWorker`.
  4. A second recovery sweep is a no-op (run is no longer stale) — recovery
     cannot double-requeue.
  5. The second (post-restart) worker instance claims the now-`Pending` run
     exactly once; a third concurrent claim attempt against the
     now-`Running` run returns `null`.

### 10.3 Faulted worker
- `Worker_SurvivesTransientClaimFailure_AndKeepsPolling` — a repository
  that throws once then succeeds proves `OrchestrationRunWorker.ExecuteAsync`'s
  `try/catch (Exception ex)` around `PollOnceAsync` keeps the polling loop
  alive after a single dependency exception (matches the production
  comment: *"A single poll failing must never take the whole loop down"*).
- `WorkerHealthCoordinator_IsNotAutomaticallyMarkedFaulted_ByOrchestrationRunWorkerPollFailures` —
  pins the **actual** (not spec-aspirational) contract. See Finding below.

**Finding (reported, not silently fixed):** spec section 10.3 asks that
"health signal reflects failure" when an unhandled dependency failure is
injected. In the current implementation, `OrchestrationRunWorker` has no
reference to `IWorkerHealthCoordinator` at all — health transitions are
driven exclusively by `WorkerHealthLifecycleHostedService`, which only
reacts to `IHostApplicationLifetime.ApplicationStarted` /
`ApplicationStopping`. A transient (or even repeated) claim failure inside
`OrchestrationRunWorker.ExecuteAsync` is caught, logged via
`ILogger.LogError`, and the loop continues — no call to
`IWorkerHealthCoordinator.MarkFaulted` and no `IOperationalMetrics` counter
records it (there is no "poll failure" metric in `IOperationalMetrics`
today). This means an external readiness/liveness probe reading
`WorkerHealthCoordinator` state would not see repeated claim failures
reflected as degraded health. This is a real gap between the health file
the container orchestrator reads and the worker's actual poll-loop error
rate — flagged here rather than patched, per this task's scope (test-only
change, no production edits).

## 9. Container Failure Tests (manual runbook — spec section 11)

These were **not** implemented as automated xunit tests. They were validated
manually via `docker compose config` / `build` / `up` by the coordinating
session. Documented here as a checklist, not as automated coverage:

- [ ] **A — Normal startup**: `migrator` completes, then API and Worker
      start; readiness becomes healthy.
- [ ] **B — Migration failure**: force a migration failure; migrator exits
      non-zero; API and Worker do not start; no connection-string leakage
      in diagnostics output.
- [ ] **C — API restart**: restart the API container; persistent database
      remains intact; migrations are not re-run/corrupted; readiness
      returns.
- [ ] **D — Worker restart**: restart the Worker container both idle and
      mid-execution; health files (`worker.ready` / `worker.live`) are
      recreated correctly; readiness transitions correctly; persistent
      state stays consistent.
- [ ] **E — Dependency volume persistence**: stop and recreate containers
      without deleting volumes; persisted database data remains available.

Run with:

```powershell
docker compose config
docker compose build
docker compose up -d
# then manually exercise A–E and inspect container logs / health files
```

## 10. Acceptance Criteria (spec section 15, summarized)

- **Stability** — no deadlocks, no hangs, no unbounded queue growth, no
  leaked bulkhead permits, no corrupted persistent state.
- **Correct degradation** — under overload, rejections are controlled
  (429/503/504), not unexpected 500s.
- **Recovery** — after load or dependency recovery: circuits return to
  Closed, queues drain, worker health returns appropriately, database
  operations recover, API readiness stays correct.

No absolute-performance thresholds are asserted (deliberately, per spec) —
all acceptance is behavioral/relative (e.g. "count returns to zero", "claim
count stops increasing", "recovered count is exactly one").

## 11. Running Fast Tests

```powershell
dotnet build apps/AI.Factory.slnx
dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s
```

**No trait-based exclusion is configured anywhere in the solution** (no
`.runsettings`, no default `--filter` wired into a script or CI config as
observed from this work-stream). This means: **all new `Resilience`,
`Stress`, and `Load`-tagged tests currently run as part of the default
`dotnet test` invocation** alongside everything else — the Category traits
exist for *selective* filtering, not automatic exclusion. If the intent is
for `dotnet test apps/AI.Factory.slnx` to stay fast/deterministic per spec
section 2, a `.runsettings` file or CI-level `--filter "Category!=Stress&Category!=Load"`
would need to be introduced separately — that was not done here (adding it
would be a production/CI-config change outside a single test-file task, and
should be a deliberate follow-up decision by whoever owns the CI
configuration).

## 12. Running Expensive Tests Explicitly

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=Resilience"
dotnet test apps/AI.Factory.slnx --filter "Category=Stress"
dotnet test apps/AI.Factory.slnx --filter "Category=Load"
```

## 13. Known Limitations

- **No controllable time abstraction for circuit breaker HalfOpen timing**:
  spec section 6.3 explicitly discourages `Thread.Sleep` for long durations
  and suggests a controllable time abstraction. As implemented in
  `CircuitBreakerStressTests.cs`, break-duration advancement relies on short
  real delays rather than a fake/injectable clock — acceptable for the
  short break durations configured in tests, but not a true deterministic
  time abstraction. (Observed from the Application.Tests work-stream file;
  confirm against that file directly if precision matters.)
- **No container-restart automation**: section 9 above is a manual runbook,
  not automated xunit coverage — there is no Testcontainers-based or
  scripted Docker Compose restart test in this codebase as of this phase.
- **Worker restart tests use an in-memory fake, not a real second process**:
  `WorkerRestartRecoveryTests.cs` simulates "two worker instances" as two
  `OrchestrationRunWorker` objects sharing one in-memory fake repository
  within a single test process — it does not spin up two real OS processes
  or a real SQLite file with actual row-level locking. This is consistent
  with the rest of the Worker.Tests suite (`OrchestrationRunWorkerShutdownTests.cs`
  uses the same style) but means true multi-process lease-contention races
  are not exercised here (real SQLite contention is instead covered, where
  it exists, under the Application/Api test projects' database stress
  tests).
- **Worker health signal gap**: see the Finding in section 8 — transient
  poll failures are not reflected in `IWorkerHealthCoordinator` or
  `IOperationalMetrics` today. Tests pin this actual behavior rather than
  an aspirational one; closing the gap would be a production-code change
  outside this task's scope.
- **No default-suite exclusion for expensive categories**: see section 11 —
  `Stress`/`Load` tests run in the default `dotnet test` invocation today.
- Sections on other work-streams' limitations (Application `Stress`, Api
  `Load`/`DatabaseConcurrencyTests`) are filled in only to the extent
  observable from reading their test files at the time this document was
  written; re-check those files directly for anything added afterward.
