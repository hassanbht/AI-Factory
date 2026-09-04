# Phase 17.4.12 — Deterministic Resilience, Time Abstraction & Production Cleanup

## 1. Why TimeProvider was introduced

Phase 17.4.11 documented that the production circuit breaker's break-duration/half-open transition logic
used `DateTime.UtcNow` directly, making tests either rely on short real `Task.Delay` waits (flaky under
load) or skip timing assertions altogether. `System.TimeProvider` (BCL since .NET 8, no extra package
required) is the standard fix: production code depends on an injectable clock abstraction, production DI
supplies `TimeProvider.System`, and tests supply a deterministic, advanceable clock instead of waiting on
the wall clock.

## 2. Canonical production circuit breaker

`AI.Factory.Application.Resilience.CircuitBreakerRegistry.CircuitBreakerImpl` — registered via
`AddSingleton<ICircuitBreakerRegistry, CircuitBreakerRegistry>()` in both `Api/Program.cs` and
`Worker/Program.cs`, and the type every dependency actually routes through via `ResilienceExecutor`. This
is the implementation this phase modified. (`AI.Factory.Infrastructure.Resilience.CircuitBreaker` — the
other implementation Phase 17.4.11 flagged as unwired — is removed; see §6.)

## 3. TimeProvider architecture

```
Production:
  Api/Program.cs, Worker/Program.cs
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ICircuitBreakerRegistry, CircuitBreakerRegistry>();
        -> CircuitBreakerRegistry(TimeProvider? timeProvider = null) resolves TimeProvider from DI
        -> passes it into each CircuitBreakerImpl it creates

Testing:
  apps/tests/AI.Factory.Application.Tests/ManualTimeProvider.cs
    sealed class ManualTimeProvider : TimeProvider
        override GetUtcNow() => _now
        void Advance(TimeSpan delta) => _now += delta
  new CircuitBreakerRegistry(metrics: _metrics, timeProvider: time)
```

`ManualTimeProvider` is a ~15-line local subclass, not the
`Microsoft.Extensions.TimeProvider.Testing` NuGet package — `CircuitBreakerImpl` only ever calls
`TimeProvider.GetUtcNow()` (never `CreateTimer` or high-resolution timestamps), so the smallest correct
implementation is one overridden method. Pulling in a new test package for that would have been unnecessary
dependency weight (`IMPORTANT IMPLEMENTATION RULES`: "Avoid unnecessary dependencies").

Both `CircuitBreakerRegistry`'s own constructor and the nested `CircuitBreakerImpl`'s constructor take
`TimeProvider? timeProvider = null` as their last parameter, defaulting to `TimeProvider.System` — this
kept every existing call site (`new CircuitBreakerRegistry(metrics: _metrics)`,
`new CircuitBreakerRegistry(null, metrics)` in `ResilienceExecutor`'s fallback, and every other production
and test construction) source-compatible. No constructor ambiguity was introduced — there is exactly one
constructor per class.

## 4. Circuit breaker changes

`CircuitBreakerImpl` had exactly two `DateTime.UtcNow` call sites, both replaced with
`_timeProvider.GetUtcNow().UtcDateTime`:

- `CheckHalfOpenTransitionUnderLock()` — the Open → HalfOpen boundary check (`elapsed >= BreakDuration`).
- `TransitionToUnderLock(CircuitState.Open)` — stamping `_openedAtUtc` when the circuit opens (both from
  Closed and from a failed HalfOpen probe).

State machine behavior is unchanged — same transitions, same half-open admission counting
(`_halfOpenTrialsIssued < HalfOpenTrialCount`), same "only the final logical operation result affects
circuit health" behavior from `ResilienceExecutor`'s retry-then-circuit-record flow (nothing in that flow
was touched by this phase). `CircuitBreakerSnapshot.OpenedAtUtc`/`NextProbeAtUtc` are still `DateTime` — the
public contract is unchanged, they're just now sourced from the injected clock instead of the wall clock,
satisfying the "must remain backward compatible" / "do not expose internal test clock details" observability
constraints.

## 5. Deterministic test improvements

`CircuitBreakerRegistryTests.cs` — 3 existing tests previously used `await Task.Delay(100)` (with a 50ms
break duration) to wait out the break window:

- `BreakDurationElapses_TransitionsToHalfOpen_AllowsTrials`
- `HalfOpen_SuccessClosesCircuit`
- `HalfOpen_FailureReopensCircuitImmediately`

All three now construct a `ManualTimeProvider`, use a 30-second `BreakDuration` (a value no real-time test
would ever wait out), and call `time.Advance(TimeSpan.FromSeconds(30))` instead of delaying. Each converted
from `async Task` to a synchronous `void` test since there's no real await left.

`Stress/CircuitBreakerStressTests.cs` — 4 of 5 tests had real `Task.Delay` calls (30–40ms waits against
15–30ms break durations, i.e. asserting timing behavior on margins as thin as double the break duration):

- `OpenToHalfOpen_AllowsOnlyConfiguredProbeCount_UnderConcurrentPressure`
- `HalfOpenToClosed_ProbeSucceeds_ResetsFailureCount`
- `HalfOpenToOpen_ProbeFails_ReopensImmediately_AndRestartsBreakDuration`
- `RepeatedOpenCloseCycles_NoStateLeak_AcrossManyIterations` (25 iterations × 30ms delay ≈ 750ms of real
  wall-clock time previously)

All four now take a `ManualTimeProvider`, advance past the break duration deterministically, and (for the
concurrency-race test) still use real `Task.Run`/thread-pool concurrency where that's the actual thing under
test — advancing a clock doesn't change what needs verifying about a `Parallel.For`/`Task.Run` race, so
those weren't touched.

New deterministic tests added to `CircuitBreakerRegistryTests.cs`, covering the required list:

- `Open_RemainsOpen_BeforeBreakDurationExpires` — advances to 1ms *before* the boundary, proving no early
  transition.
- `IndependentDependencies_MaintainIndependentState_OnSharedClock` — two named breakers on one registry (one
  shared `TimeProvider`, since the registry owns one clock) prove per-dependency state (failures, Open/Closed)
  never leaks across dependency names.
- `ConcurrentCallers_AtTransitionBoundary_RemainThreadSafe` — 50 concurrent `TryEnter()` calls exactly at the
  break-duration boundary against a `HalfOpenTrialCount` of 3, asserting exactly 3 admissions — the existing
  lock in `TryEnter`/`CheckHalfOpenTransitionUnderLock` was already correct; this pins it under the
  deterministic clock rather than a timing-dependent race.

Existing tests already covering "successful probe closes", "failed probe reopens", "success resets
consecutive failures", "half-open trial count enforced", and general concurrent safety
(`ConcurrentSafety_MultipleThreadsRecordingOutcomes`) needed no behavioral change, only (where they used
delays) the same clock-advance conversion.

Verified stable: `CircuitBreakerRegistryTests` (12 tests) ran in 79ms then 71ms across two consecutive runs;
`CircuitBreakerStressTests` (5 tests) ran in 46ms then 35ms — both roughly an order of magnitude faster than
their previous real-delay equivalents (the old stress-test file alone summed to ~750ms of intentional
`Task.Delay` in one test), and with zero variance in outcome across repeated runs.

## 6. SQLite resilience

**Policy-level deterministic tests** (the actual deliverable — production `DatabaseResiliencePolicy` code,
unmodified, driven directly): `DatabaseResiliencePolicyTests.cs` already had `SqliteException(message, 5)`
(Busy) retry/succeed and retry-exhaustion coverage from Phase 17.4.11. This phase added, using the same
real-`SqliteException`-construction pattern (no fake provider, no mocked `DbContext`):

- `Classify_SqliteLocked_IsNeverConfusedWithBusy` — codes 5 and 6 classify distinctly.
- `ExecuteAsync_Locked_RetriesAndSucceeds` / `ExecuteAsync_Locked_ExhaustedRetries_ThrowsAndRecordsFailure`
  — mirrors the existing Busy coverage for `SQLITE_LOCKED` (code 6).
- `ExecuteAsync_Busy_BackoffDelayGrows_AcrossRetries` — proves `ComputeDelay`'s exponential backoff is
  actually awaited between attempts (not skipped): 2 retries at a 40ms initial delay must sum to at least
  90ms elapsed (a floor well under the ~120ms expected mean, wide enough to absorb the policy's documented
  ±20% jitter without flaking, but high enough that a skipped backoff — ~0ms — still fails it).
- `ExecuteAsync_NonTransientDbException_IsNotClassifiedAsBusyOrLocked_AndDoesNotRetry` — a custom
  `DbException` with `IsTransient => false` is classified as neither Busy nor Locked, isn't retried (1
  attempt, 0 retries recorded), and its exact exception type propagates unchanged.
- `ExecuteAsync_CancelledDuringBackoffDelay_StopsImmediately_NoFurtherAttempt` — a 500ms initial retry delay
  against a 50ms `CancelAfter`: the first attempt throws Busy, the policy enters its backoff `Task.Delay`,
  cancellation fires mid-delay, and exactly 1 attempt ever ran (asserted via `Assert.ThrowsAnyAsync` since
  `Task.Delay`'s cancellation surfaces as `TaskCanceledException`, a subtype of `OperationCanceledException`
  rather than an exact match).

All 13 tests in `DatabaseResiliencePolicyTests.cs` passed on 3 consecutive runs (297ms/368ms/381ms).

**Real integration tests — explicitly distinguished, not overclaimed**:
`DatabaseConcurrencyStressTests.cs`/`ConcurrentApiLoadTests.cs` already exercise real concurrent SQLite
access through the full HTTP stack; they are supplemental coverage, not deterministic, and this phase did
not add new ones (the brief: "Do not create flaky timing-based tests merely to claim full integration
coverage" and "Do not pretend that a fully real SQLite lock-contention test is deterministic if it is not").
**A pre-existing flake was found, not introduced, while running this suite** — see §10.

## 7. Dead code cleanup

Investigated before removing anything (per the workstream's required steps): grepped the full solution for
`CircuitBreakerAgentExecutorDecorator` and `AI.Factory.Infrastructure.Resilience.CircuitBreaker` usage.

Findings:
- Neither type appears in `Api/Program.cs` or `Worker/Program.cs` DI registration (confirmed in Phase
  17.4.11 and re-confirmed here).
- `CircuitBreakerAgentExecutorDecorator : IAgentExecutor` was never registered as a decorator over the real
  `IAgentExecutor` implementation anywhere — `HermesAgentExecutor` is used directly.
- The only consumers of either class, or of `CircuitBreakerOptions` (a third file the dead `CircuitBreaker`
  depended on), were their own test file (`CircuitBreakerTests.cs`) and stale `TestResults/*.xml` artifacts.
- `AI.Factory.Application.Resilience.CircuitOpenException` (used by `ApiExceptionMapper`,
  `GlobalExceptionMiddlewareTests`, `RetryExecutorTests`, `HttpOpenVikingClient`) is a **different, actively
  used** type in a different namespace — an earlier broad grep for "Infrastructure.Resilience" surfaced these
  as false positives; they were re-checked and don't reference the dead classes.

**Removed** (git-tracked deletion, fully recoverable from history if ever needed):
- `apps/src/AI.Factory.Infrastructure/Resilience/CircuitBreaker.cs`
- `apps/src/AI.Factory.Infrastructure/Resilience/CircuitBreakerOptions.cs`
- `apps/src/AI.Factory.Infrastructure/Agents/CircuitBreakerAgentExecutorDecorator.cs`
- `apps/tests/AI.Factory.Application.Tests/CircuitBreakerTests.cs` (tested only the removed class)

No production code referenced any of the four files — `dotnet build apps/AI.Factory.slnx` was clean (0
errors, 0 warnings) immediately after deletion, with no compensating edits required anywhere else. This
directly resolves the Phase 17.4.11-documented risk: "a legacy CircuitBreaker implementation... tested as if
it were production" — it no longer exists to be mistaken for production code.

## 8. DI registration changes

- `Api/Program.cs`: added `builder.Services.AddSingleton(TimeProvider.System);` immediately before the
  existing `AddSingleton<ICircuitBreakerRegistry, CircuitBreakerRegistry>()` line.
- `Worker/Program.cs`: identical addition, same position.
- No lifetime changes to `ICircuitBreakerRegistry` (still `Singleton`, unchanged — a per-host shared
  registry is correct, per-dependency circuits persist for the process lifetime).
- `TimeProvider.System` is itself effectively stateless/immutable, so registering it `Singleton` alongside a
  `Singleton` `CircuitBreakerRegistry` introduces no scoped-captured-by-singleton risk, and no fake time can
  reach production — `ManualTimeProvider` lives only in the test project and is never referenced from
  `apps/src`.
- Verified by running the full `Api.Tests` and `Worker.Tests` suites (both spin up their real composition
  roots via `WebApplicationFactory<Program>` / `Program.ConfigureServices`) — no DI resolution errors in
  either host.

## 9. Documentation fixes

- `.ai/context/phase-17.4.11-production-findings-remediation.md` §15 previously stated "Docker Compose /
  container build / live smoke validation — not executed in this session" directly beneath §14, which
  already recorded a successful `docker compose config/build/up` run and live smoke test. Corrected: the
  stale bullet is struck through with an explanatory note rather than silently deleted, so the historical
  record of the contradiction (and its correction) stays visible.

## 10. Remaining limitations

Be explicit, per the brief's own instruction not to claim a limitation resolved without automated proof:

- **Circuit breaker time abstraction (Finding 5, Phase 17.4.11) — resolved.** `CircuitBreakerImpl` no longer
  references `DateTime.UtcNow`; `CircuitBreakerRegistryTests` and `CircuitBreakerStressTests` no longer wait
  on real time for break-duration/half-open assertions. Verified stable across repeated runs (§5).
- **SQLite busy/locked deterministic coverage (Finding 6, Phase 17.4.11) — resolved at the policy level.**
  `DatabaseResiliencePolicy`'s classification and retry/backoff/cancellation logic now has deterministic,
  repeatable, real-`SqliteException`-driven coverage for both BUSY and LOCKED, non-transient-failure
  exclusion, and mid-backoff cancellation. This was always the honest ceiling stated in the brief — full
  real-provider lock-contention reproduction through the entire HTTP stack remains inherently non-deterministic
  and was never claimed otherwise.
- **Newly surfaced (not introduced) flake**: `DatabaseConcurrencyStressTests.ConcurrentDuplicateSlugCreates_ExactlyOneSucceeds_NoDuplicateRowsPersisted`
  failed intermittently while validating this phase (passed 1 of 3 standalone runs; failed both full-suite
  runs) — expected 409 Conflict, got 500 Internal Server Error on a losing concurrent writer. This test was
  not touched by this phase (last modified in the Phase 17.4.10 era per its own comment) and its failure mode
  is consistent with `HighThroughputApiFactory`'s test-only design: all concurrent requests share one
  already-open `SqliteConnection` object rather than a connection string, so concurrent `SaveChangesAsync`
  calls can race on that single ADO.NET connection handle in ways a real multi-connection deployment would
  not. This is a test-infrastructure concern, not a production `ApiExceptionMapper`/`DatabaseResiliencePolicy`
  defect — no production code path changed in this phase touches that classification. It was not fixed here:
  diagnosing and fixing shared-connection test concurrency is a distinct problem from this phase's five
  workstreams, and attempting a same-session fix risked destabilizing a previously-passing test suite for a
  finding outside this phase's scope. Flagged for separate follow-up.
- **Dead code cleanup was narrowly scoped.** Only the three files with zero consumers and their dedicated
  test file were removed. No other resilience code was audited for dead paths in this pass.

## Files created

- `apps/tests/AI.Factory.Application.Tests/ManualTimeProvider.cs`
- `.ai/context/phase-17.4.12-deterministic-resilience-production-cleanup.md` (this file)

## Files modified

- `apps/src/AI.Factory.Application/Resilience/CircuitBreakerRegistry.cs`
- `apps/src/AI.Factory.Api/Program.cs`
- `apps/src/AI.Factory.Worker/Program.cs`
- `apps/tests/AI.Factory.Application.Tests/CircuitBreakerRegistryTests.cs`
- `apps/tests/AI.Factory.Application.Tests/Stress/CircuitBreakerStressTests.cs`
- `apps/tests/AI.Factory.Api.Tests/DatabaseResiliencePolicyTests.cs`
- `.ai/context/phase-17.4.11-production-findings-remediation.md`

## Files removed

- `apps/src/AI.Factory.Infrastructure/Resilience/CircuitBreaker.cs`
- `apps/src/AI.Factory.Infrastructure/Resilience/CircuitBreakerOptions.cs`
- `apps/src/AI.Factory.Infrastructure/Agents/CircuitBreakerAgentExecutorDecorator.cs`
- `apps/tests/AI.Factory.Application.Tests/CircuitBreakerTests.cs`

## Validation

```
dotnet build apps/AI.Factory.slnx                → 0 errors, 0 warnings

dotnet test apps/AI.Factory.slnx --no-build --blame-hang-timeout 90s   (run 1 and run 2, identical both times)
  → AI.Factory.Worker.Tests:      60/60 passed
  → AI.Factory.Application.Tests: 365/365 passed
  → AI.Factory.Api.Tests:         191/192 passed — 1 pre-existing flake, see §10 (not introduced this phase)

Repeated timing-sensitive test runs (stability check):
  CircuitBreakerRegistryTests  → 12/12 passed, twice (79ms, 71ms)
  CircuitBreakerStressTests    → 5/5 passed, twice (46ms, 35ms)
  DatabaseResiliencePolicyTests → 13/13 passed, three times (297ms, 368ms, 381ms)
```
