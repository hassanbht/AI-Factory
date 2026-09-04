# Phase 17.4.12 — Deterministic Resilience, Time Abstraction & Production Cleanup

## Context

You are continuing the AI Factory production-readiness implementation.

Previous phases 17.4.1 through 17.4.11 established:

- Centralized ProblemDetails error handling
- Correlation IDs
- Retry and timeout resilience
- Circuit breakers
- Bulkhead isolation
- Graceful shutdown and cancellation propagation
- Security hardening
- Secret management
- Database resilience and optimistic concurrency
- Production Docker/container readiness
- Load, stress and failure testing
- Production runbooks
- Production findings remediation

Phase 17.4.11 resolved several real production findings, but explicitly documented the following remaining gaps:

1. Circuit breaker time behavior is not deterministic because the production circuit breaker still uses DateTime.UtcNow.
2. Circuit breaker tests rely on short real Task.Delay calls.
3. Deterministic full-stack SQLite SQLITE_BUSY / SQLITE_LOCKED reproduction remains unresolved.
4. A legacy CircuitBreaker implementation and CircuitBreakerAgentExecutorDecorator are not wired into production DI.
5. Documentation contains at least one validation/limitations inconsistency regarding Docker smoke testing.

This phase must resolve or deliberately simplify these remaining reliability and cleanup concerns.

Do not redesign the entire resilience architecture.

Preserve Clean Architecture.

The canonical production resilience implementation is:

AI.Factory.Application.Resilience.CircuitBreakerRegistry.CircuitBreakerImpl

Any work must target the actual production implementation first.

--------------------------------------------------
## PRIMARY OBJECTIVE
--------------------------------------------------

Make resilience behavior deterministic, testable, and free from misleading or dead production code.

This phase has five workstreams:

A. Time abstraction for production circuit breakers
B. Deterministic circuit breaker tests
C. SQLite busy/locked resilience testing improvement
D. Dead resilience code cleanup
E. Production documentation consistency

--------------------------------------------------
# WORKSTREAM A — TIME ABSTRACTION
# Circuit Breaker Deterministic Time
--------------------------------------------------

## Goal

Remove direct DateTime.UtcNow dependency from the production circuit breaker state machine.

The circuit breaker must use an injectable time abstraction.

Prefer the modern .NET approach:

System.TimeProvider

Use TimeProvider rather than introducing a custom clock abstraction unless there is a strong architectural reason not to.

The design must support:

- Production TimeProvider.System
- Deterministic test-controlled time
- Dependency injection
- No static global mutable clock state

--------------------------------------------------
## Required implementation
--------------------------------------------------

### 1. Update CircuitBreakerRegistry

Inspect:

AI.Factory.Application/Resilience/CircuitBreakerRegistry.cs

Identify every usage of:

DateTime.UtcNow

or equivalent direct wall-clock access.

Replace them with a TimeProvider dependency.

The registry should receive TimeProvider through DI.

Example conceptual direction:

public CircuitBreakerRegistry(
    ...existing dependencies...,
    TimeProvider timeProvider)

Do not introduce breaking constructor ambiguity.

Production DI must register:

TimeProvider.System

as the default.

--------------------------------------------------
### 2. CircuitBreakerImpl must use injected time

The actual circuit breaker implementation must use the registry/provider clock for:

- circuit open timestamp
- break duration expiration
- Open -> HalfOpen transition
- half-open probe timing if applicable
- snapshot timestamps

Do not leave hidden DateTime.UtcNow calls inside the nested implementation.

Search the entire relevant resilience path to ensure no production circuit state transition still depends on real time directly.

--------------------------------------------------
### 3. Preserve existing behavior

The state machine behavior must remain semantically identical:

Closed
  -> consecutive failures reach threshold
  -> Open

Open
  -> break duration expires
  -> HalfOpen

HalfOpen
  -> successful probe
  -> Closed

HalfOpen
  -> failure
  -> Open

Half-open admission limits must remain correct.

Existing Option B behavior must remain unchanged:

Transient failures recovered by retry do not degrade circuit health.

Only the final logical operation result should influence circuit health.

--------------------------------------------------
# WORKSTREAM B — DETERMINISTIC CIRCUIT BREAKER TESTING
--------------------------------------------------

## Goal

Remove timing flakiness from circuit breaker tests.

Tests must not use:

Task.Delay(...)

to wait for a circuit break duration to expire.

--------------------------------------------------
## Requirements

### 1. Test TimeProvider

Use one of these approaches:

Preferred:
- Microsoft.Extensions.TimeProvider.Testing FakeTimeProvider

Alternative:
- Small local test TimeProvider implementation

Choose the smallest clean dependency footprint.

Do not introduce a custom clock abstraction if TimeProvider already solves the problem.

--------------------------------------------------
### 2. Rewrite timing-dependent tests

Inspect all tests involving:

- CircuitBreakerRegistry
- CircuitBreaker
- half-open transitions
- break duration
- probe admission
- concurrent state transitions

Replace real waiting with deterministic clock advancement.

Example conceptual behavior:

fakeTime.Advance(TimeSpan.FromSeconds(31));

Then assert transition behavior immediately.

--------------------------------------------------
### 3. Add deterministic tests

Ensure explicit tests for:

1. Circuit remains Open before BreakDuration.
2. Circuit transitions to HalfOpen exactly when break duration expires.
3. No transition occurs before the configured boundary.
4. HalfOpen trial count is enforced.
5. Successful probe closes the circuit.
6. Failed probe immediately reopens it.
7. Success resets consecutive failures.
8. Multiple dependencies maintain independent clocks/state.
9. Concurrent callers at a transition boundary remain thread-safe.
10. No real-time delay is required.

The test suite must run reliably under repeated execution.

--------------------------------------------------
# WORKSTREAM C — SQLITE BUSY / LOCKED RESILIENCE
--------------------------------------------------

## Goal

Improve confidence in handling SQLite:

- SQLITE_BUSY (5)
- SQLITE_LOCKED (6)

Do not create flaky timing-based tests merely to claim full integration coverage.

The goal is deterministic confidence.

--------------------------------------------------
## Step 1 — Inspect current architecture

Inspect:

DatabaseResiliencePolicy
DatabaseFailureClassification
FactoryDbContext
EF Core SQLite configuration
Connection string configuration
Database resilience tests

Determine exactly where SQLite busy/locked exceptions enter the application.

--------------------------------------------------
## Step 2 — Preferred deterministic testing strategy

Introduce a deterministic test seam if necessary.

Acceptable approaches include:

A. Construct real SqliteException instances where supported by the provider APIs and test the policy directly.

B. Add an internal/test-visible failure classification seam.

C. Introduce a narrowly scoped infrastructure abstraction around transient database execution.

D. Use a controlled provider/test double only around the exception-producing boundary.

Avoid mocking EF Core DbContext behavior broadly.

Do not add a fake database provider.

The actual DatabaseResiliencePolicy classification and retry decision logic must remain production code.

--------------------------------------------------
## Required tests

Add deterministic coverage proving:

### SQLITE_BUSY

- classified as retryable database failure
- retry count respects configured maximum
- exponential backoff calculation is invoked
- eventual success works
- retry exhaustion returns the expected final failure

### SQLITE_LOCKED

Same coverage.

### Non-transient database failures

Must:

- not be incorrectly retried
- preserve existing error mapping
- not be classified as SQLITE_BUSY/LOCKED

### Cancellation

Cancellation during database retry backoff must stop immediately.

No additional retry should start after cancellation.

--------------------------------------------------
## Important constraint

Do not pretend that a fully real SQLite lock-contention test is deterministic if it is not.

If a real provider-level test remains useful, keep it as supplemental integration coverage.

The deterministic test must validate the application resilience path without depending on scheduler timing.

--------------------------------------------------
# WORKSTREAM D — DEAD RESILIENCE CODE CLEANUP
--------------------------------------------------

## Goal

Remove or explicitly retire resilience code that is not part of the production execution path.

Phase 17.4.11 identified:

AI.Factory.Infrastructure.Resilience.CircuitBreaker

and:

CircuitBreakerAgentExecutorDecorator

as not registered in either API or Worker DI container.

--------------------------------------------------
## Required investigation

Before deleting anything:

1. Search the entire solution.
2. Identify:
   - direct callers
   - DI registrations
   - tests
   - documentation references
3. Determine whether the code is:
   - production code
   - obsolete implementation
   - compatibility layer
   - test-only utility

--------------------------------------------------
## Preferred outcome

If the code is genuinely obsolete:

- remove it
- remove obsolete tests
- update namespaces/imports
- remove stale documentation references

Do not preserve dead code merely because tests reference it.

Tests should target the canonical production implementation.

--------------------------------------------------
## Alternative

If removal would break a legitimate external extension point:

- document the reason
- mark the legacy path explicitly
- prevent future accidental production registration
- add architecture documentation explaining why both implementations exist

But deletion is preferred if there is no real consumer.

--------------------------------------------------
# WORKSTREAM E — DOCUMENTATION CONSISTENCY
--------------------------------------------------

## Required review

Inspect:

.ai/context/phase-17.4.11-production-findings-remediation.md

Correct contradictions.

Specifically verify consistency between:

- Validation section
- Remaining limitations
- Docker validation claims

If Docker compose build/up/live smoke tests were executed successfully, the documentation must not simultaneously claim they were not executed.

--------------------------------------------------
## Update documentation

Create:

.ai/context/phase-17.4.12-deterministic-resilience-production-cleanup.md

Document:

1. Why TimeProvider was introduced
2. Which production circuit breaker implementation is canonical
3. How deterministic time testing works
4. Circuit breaker state machine verification
5. SQLite busy/locked deterministic test strategy
6. Which legacy resilience code was removed or retained
7. DI registration changes
8. Test reliability improvements
9. Documentation corrections
10. Remaining limitations, if any

Do not claim a limitation is resolved unless automated tests demonstrate it.

--------------------------------------------------
# OBSERVABILITY
--------------------------------------------------

Do not introduce unnecessary new metrics.

Existing operational metrics should remain stable.

If any circuit breaker timestamps are exposed through diagnostics:

- they must now originate from TimeProvider
- behavior must remain backward compatible

Do not expose internal test clock details.

--------------------------------------------------
# API CONTRACT SAFETY
--------------------------------------------------

This phase should not change public API routes.

Do not change:

- ProblemDetails contract
- correlation ID behavior
- security headers
- operational endpoint routes

unless required to fix an actual defect.

This is an internal reliability/testability cleanup phase.

--------------------------------------------------
# DI / LIFETIME REQUIREMENTS
--------------------------------------------------

Verify:

API composition root
Worker composition root

both resolve:

- TimeProvider
- ICircuitBreakerRegistry
- resilience services

without lifetime violations.

Avoid:

- scoped service captured by singleton
- fake time leaking into production
- duplicate singleton circuit registries

The registry must remain shared appropriately per host.

--------------------------------------------------
# TEST REQUIREMENTS
--------------------------------------------------

Run:

dotnet build apps/AI.Factory.slnx

Then:

dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s

Also run relevant categories separately where applicable.

At minimum:

Circuit breaker tests
Database resilience tests
Worker tests
API tests

Repeat the timing-sensitive resilience tests at least twice to verify deterministic stability.

--------------------------------------------------
# ACCEPTANCE CRITERIA
--------------------------------------------------

Phase 17.4.12 is complete only when:

1. Production CircuitBreakerRegistry no longer directly depends on DateTime.UtcNow for state transitions.
2. Production DI uses TimeProvider.System.
3. Tests can control circuit breaker time deterministically.
4. Circuit breaker tests contain no real waiting solely for break-duration expiration.
5. Circuit state transition tests pass without Task.Delay-based timing assumptions.
6. SQLite BUSY/LOCKED classification and retry behavior have deterministic automated coverage.
7. Cancellation during database retry backoff is tested.
8. Non-transient database failures are proven not to retry.
9. Obsolete/unwired resilience code is removed or explicitly justified.
10. No dead implementation remains accidentally tested as if it were production.
11. API public contracts remain unchanged.
12. Documentation contradictions are corrected.
13. Build succeeds with zero new warnings or errors.
14. Full test suite passes.
15. Timing-sensitive tests are stable across repeated execution.

--------------------------------------------------
# FINAL REPORT
--------------------------------------------------

Produce a detailed walkthrough containing:

## 1. Summary

What Phase 17.4.12 improved.

## 2. TimeProvider Architecture

Show:

Production:
TimeProvider.System

Testing:
Fake/controlled TimeProvider

Explain dependency flow.

## 3. Circuit Breaker Changes

Explain exact production implementation changed.

Include state transition behavior.

## 4. Deterministic Test Improvements

List tests that previously depended on real delays.

Explain how deterministic time advancement replaced them.

## 5. SQLite Resilience

Explain deterministic BUSY/LOCKED testing strategy.

Clearly distinguish:

- policy-level deterministic tests
- real SQLite integration tests

Do not overclaim.

## 6. Dead Code Cleanup

List removed or retained legacy classes and why.

## 7. DI Validation

Confirm API and Worker composition roots.

## 8. Documentation Fixes

List corrected contradictions.

## 9. Validation

Include exact commands and:

- build result
- test counts per project
- total passing tests
- repeated timing-sensitive test results

## 10. Remaining Limitations

Be explicit.

Do not claim "Production Ready" if a material unresolved gap remains.

--------------------------------------------------
# IMPORTANT IMPLEMENTATION RULES
--------------------------------------------------

- Inspect existing architecture before modifying it.
- Do not create parallel resilience systems.
- Do not introduce a custom IClock unless TimeProvider is insufficient.
- Prefer deterministic tests over sleeps.
- Preserve existing Clean Architecture boundaries.
- Preserve all existing security behavior.
- Preserve existing API contracts.
- Avoid unnecessary dependencies.
- Do not silently weaken resilience behavior to make tests pass.
- Do not leave legacy circuit breaker code tested while the real production implementation remains untested.
- Every new production behavior must have focused automated tests.