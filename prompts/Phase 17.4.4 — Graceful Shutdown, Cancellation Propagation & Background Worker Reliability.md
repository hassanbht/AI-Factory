# Phase 17.4.4 — Graceful Shutdown, Cancellation Propagation & Background Worker Reliability

You are continuing implementation of the AI Factory platform.

IMPORTANT:
This is an existing production-oriented Clean Architecture solution.
Do NOT redesign existing architecture.
Do NOT replace existing abstractions with new duplicate concepts.
Extend the existing implementation.

Before making ANY changes:

1. Read AGENTS.md completely.
2. Read all relevant files under .ai/context/.
3. Read the reports/context for:
   - Phase 17.1
   - Phase 17.2
   - Phase 17.3
   - Phase 17.4.1
   - Phase 17.4.2
   - Phase 17.4.3
4. Inspect the current implementation before proposing changes.
5. Reuse existing abstractions whenever they already solve part of this problem.

The solution currently includes:

- Clean Architecture
- Domain
- Application
- Infrastructure
- API
- Worker
- EF Core / SQLite
- AutonomousCodingOrchestrator
- WorkflowEngine
- ResilienceExecutor
- Retry
- Timeout
- Circuit Breaker
- Bulkhead Isolation
- Operation Context
- Correlation IDs
- Structured Logging
- Audit Logging
- Operational Metrics
- Operational Diagnostics
- Global Exception Middleware
- ProblemDetails
- Health Checks
- Graceful API middleware pipeline
- Background Worker
- Agent execution
- Build/Test/Repair execution
- Human approval
- Evaluation
- Learning

Phase 17.4.3 is complete.

The current full test suite is green.

Do not break existing Phase 17.4.1, 17.4.2 or 17.4.3 behavior.

==================================================
GOAL
==================================================

Implement:

Phase 17.4.4
Graceful Shutdown, Cancellation Propagation & Background Worker Reliability

The system must safely handle:

- Ctrl+C
- SIGTERM
- Docker stop
- Kubernetes termination
- ApplicationStopping
- cancellation of HTTP requests
- cancellation of background operations
- shutdown during orchestration
- shutdown during agent execution
- shutdown during retry delays
- shutdown while waiting in a bulkhead queue
- shutdown while waiting for approval
- shutdown during build/test/repair

The implementation must prioritize:

1. Stop accepting new work.
2. Signal cancellation to running work.
3. Allow bounded graceful completion where appropriate.
4. Persist meaningful state.
5. Avoid corrupting workflow/task state.
6. Avoid orphaned running operations.
7. Avoid leaked SemaphoreSlim permits.
8. Avoid stuck orchestration runs.
9. Avoid duplicate execution after restart.
10. Exit within a bounded shutdown timeout.

==================================================
ARCHITECTURAL RULES
==================================================

Do NOT:

- Introduce MediatR.
- Introduce a message broker.
- Introduce Kubernetes-specific business logic.
- Introduce a second orchestration engine.
- Create a second cancellation abstraction if existing .NET CancellationToken infrastructure is sufficient.
- Catch and silently swallow OperationCanceledException.
- Convert cancellation into success.
- Mark cancelled work as successful.
- Leave running operations permanently in Running state.
- Invent distributed coordination.
- Change successful resilience behavior.

Use:

- IHostApplicationLifetime
- CancellationToken
- CancellationTokenSource
- linked cancellation tokens
- BackgroundService lifecycle
- existing operation scopes
- existing audit infrastructure
- existing metrics
- existing failure classification
- existing workflow/task/orchestration repositories

==================================================
SECTION 1 — SHUTDOWN COORDINATION ABSTRACTION
==================================================

Inspect whether an existing application shutdown abstraction already exists.

If not, introduce the smallest reusable abstraction.

Suggested shape:

IApplicationShutdown
{
    bool IsStopping { get; }

    CancellationToken StoppingToken { get; }

    bool TryBeginShutdown();

    Task WaitForShutdownAsync(CancellationToken cancellationToken = default);
}

Or another minimal design that integrates naturally with the existing architecture.

Requirements:

- Singleton lifetime.
- Thread-safe.
- Triggered by ApplicationStopping.
- Usable by API and Worker.
- Does not depend on ASP.NET HttpContext.
- Can be injected into orchestration/background execution.
- Does not duplicate CancellationToken semantics unnecessarily.

Do not over-engineer this.

==================================================
SECTION 2 — STOP ACCEPTING NEW WORK
==================================================

During shutdown:

New background work must not begin.

Inspect:

- Worker loops
- orchestration execution entry points
- project/task scheduling
- retry scheduling

Behavior:

If shutdown has started:

- do not claim new tasks
- do not start new autonomous coding executions
- do not begin new repair attempts
- do not dequeue another background item

Existing in-flight work may continue until:

- it completes
- it is cancelled
- shutdown timeout expires

Do not abruptly terminate database transactions.

==================================================
SECTION 3 — CANCELLATION PROPAGATION
==================================================

Audit the complete execution chain.

Cancellation must propagate through:

HTTP request
    ->
API endpoint
    ->
Application service
    ->
Orchestrator
    ->
Agent execution
    ->
ResilienceExecutor
    ->
Retry delay
    ->
Timeout wrapper
    ->
Provider call

and:

Worker
    ->
Background operation
    ->
Orchestrator
    ->
Build executor
    ->
Test executor
    ->
Repair execution

Use linked tokens where needed.

Example conceptual pattern:

using var linkedCts =
    CancellationTokenSource.CreateLinkedTokenSource(
        requestCancellationToken,
        shutdownStoppingToken);

var token = linkedCts.Token;

Do not create unnecessary nested CancellationTokenSource instances.

==================================================
SECTION 4 — RESILIENCE PIPELINE CANCELLATION
==================================================

Review Phase 17.4.2 and Phase 17.4.3 implementations.

Verify cancellation behavior in:

- retry delays
- exponential backoff
- operation timeout
- attempt timeout
- bulkhead waiting
- circuit breaker checks

Requirements:

Retry:

Task.Delay(delay, cancellationToken)

Bulkhead:

AcquireAsync(cancellationToken)

Timeout:

Cancellation caused by caller/shutdown must remain cancellation.

Do not misclassify external cancellation as Timeout.

Important distinction:

Caller/shutdown cancellation
    -> OperationCanceledException / Cancelled

Internal operation timeout
    -> TimeoutException / Timeout

These must remain distinguishable for:

- FailureClassifier
- Audit
- Metrics
- ProblemDetails

==================================================
SECTION 5 — GRACEFUL BACKGROUND WORKER STOP
==================================================

Inspect all Worker hosted services and loops.

Requirements:

When StopAsync / ApplicationStopping occurs:

1. Mark worker as stopping.
2. Stop acquiring new work.
3. Allow currently executing operation to observe cancellation.
4. Await in-flight operation up to configured timeout.
5. Persist final state.
6. Exit cleanly.

Do not rely on Thread.Abort or process termination.

If the worker already uses BackgroundService:

Use ExecuteAsync(stoppingToken) correctly.

If multiple operations can run concurrently:

Track active operations safely.

Suggested abstraction only if genuinely needed:

IBackgroundOperationTracker

Possible capabilities:

- Register operation
- Complete operation
- Active count
- Stop accepting work
- Wait for active operations

Do not add this if existing orchestration tracking can provide the same information.

==================================================
SECTION 6 — IN-FLIGHT OPERATION TRACKING
==================================================

The system must know which operations are currently active during shutdown.

Reuse:

- IOperationContext
- IOperationScopeFactory
- OrchestrationRun
- AuditLogEntry

Do not duplicate these concepts.

For each active orchestration operation ensure:

- OperationId exists
- CorrelationId exists
- ProjectId is known when available
- TaskId is known when available
- RunId is known when available

Shutdown diagnostics should be able to report active operations.

If a dedicated in-memory tracker is necessary, keep it minimal and thread-safe.

==================================================
SECTION 7 — ORCHESTRATION CANCELLATION SEMANTICS
==================================================

Review AutonomousCodingOrchestrator and related execution engines.

When cancellation occurs:

Do not:

- mark work item completed
- mark orchestration successful
- mark agent run successful
- continue repair
- continue retries

Instead:

1. Stop further execution.
2. Record cancellation.
3. Finalize/persist the current execution safely.
4. Leave workflow state recoverable.

Use existing domain states where possible.

If a Cancelled state already exists, use it.

If it does not exist:

Do not automatically introduce Cancelled everywhere.

First inspect existing failure/retry/workflow state design.

Prefer a recoverable state that the scheduler can detect after restart.

==================================================
SECTION 8 — SHUTDOWN TIMEOUT
==================================================

Introduce configuration.

Suggested:

Shutdown:
  GracefulTimeoutSeconds: 30
  ForceCancellationAfterSeconds: 25

Use actual project configuration conventions.

Requirements:

- Validate values.
- No hard-coded production timeout.
- Default must be conservative.
- Avoid ForceCancellationAfter > GracefulTimeout.
- Document configuration.

Behavior:

At shutdown:

T0:
ApplicationStopping triggered.

T0:
Stop accepting new work.

T0 -> configured grace period:
Allow in-flight operations to finish or observe cancellation.

Before final timeout:
Signal linked cancellation if appropriate.

At timeout:
Host is allowed to finish shutdown.

Do not attempt to kill threads.

==================================================
SECTION 9 — DATABASE CONSISTENCY DURING CANCELLATION
==================================================

Inspect EF Core usage.

Requirements:

Cancellation must not cause:

- partially committed workflow transitions
- completed state without completed result
- running state without recoverability
- duplicate execution after restart

All database writes must continue to use cancellation tokens where supported.

Example:

await dbContext.SaveChangesAsync(cancellationToken);

Do not create global transactions around the entire orchestration.

Keep transaction boundaries aligned with existing repository/service behavior.

==================================================
SECTION 10 — RECOVERY AFTER RESTART
==================================================

A process may terminate while work is running.

Implement minimal recovery behavior.

Inspect existing:

- OrchestrationRun states
- AgentRun states
- WorkItem states
- Workflow states

On Worker startup:

Identify stale in-progress work when safe.

Do not blindly retry everything.

Requirements:

- stale work must be detectable
- work must not remain permanently Running
- recovery must avoid duplicate concurrent execution
- recovery decisions must be auditable

Use timestamps already available.

If a new timestamp is truly necessary:

Add the smallest compatible schema change and migration.

Do not build a distributed scheduler.

==================================================
SECTION 11 — AUDIT EVENTS
==================================================

Reuse existing audit infrastructure.

Add audit event types only when genuinely necessary.

Required events should cover:

SHUTDOWN
- shutdown initiated
- shutdown completed
- shutdown timeout reached

OPERATION
- operation cancelled
- operation abandoned/recovered if applicable

WORKER
- worker stopping
- worker stopped

Avoid duplicate audit events.

Every relevant event should carry:

- CorrelationId when available
- OperationId when available
- ProjectId when available
- TaskId when available
- RunId when available
- Outcome
- FailureCategory where relevant

Cancellation should use:

FailureCategory.Cancelled

Do not classify cancellation as Unknown.

==================================================
SECTION 12 — OPERATIONAL METRICS
==================================================

Extend existing IOperationalMetrics only where useful.

Suggested counters:

ShutdownInitiated
ShutdownCompleted
ShutdownTimedOut

OperationsCancelled

WorkerOperationsDrained
WorkerOperationsAbandoned

Do not add meaningless counters.

Metrics must remain:

- in-memory
- thread-safe
- snapshot-based
- no raw sample persistence

==================================================
SECTION 13 — HEALTH CHECK BEHAVIOR
==================================================

During shutdown:

Readiness should not report normal readiness.

Preferred behavior:

ApplicationStopping:
    readiness -> Unhealthy or Degraded

Liveness should not necessarily fail before process termination.

Inspect existing health check conventions.

Do not break:

- /health
- /health/live
- /health/ready

The behavior must be deterministic.

==================================================
SECTION 14 — API BEHAVIOR DURING SHUTDOWN
==================================================

While shutdown is in progress:

Existing HTTP requests may complete or be cancelled.

New long-running operations should not begin.

Where a new operation is rejected because the application is stopping:

Return:

503 Service Unavailable

ProblemDetails:

type/code:
application_stopping

title:
Application is stopping

detail:
The application is shutting down and cannot accept new work.

Include:

- X-Correlation-ID
- existing ProblemDetails extensions

Do not return 500.

Add a dedicated exception only if existing abstractions cannot represent this cleanly.

==================================================
SECTION 15 — DOCKER / CONTAINER COMPATIBILITY
==================================================

Ensure behavior works correctly when receiving SIGTERM.

Do not introduce OS-specific shutdown logic.

Verify:

- .NET host shutdown token is used
- background workers observe stoppingToken
- API observes ApplicationStopping
- shutdown timeout is configurable

Do not implement Kubernetes-specific code.

==================================================
SECTION 16 — API DIAGNOSTICS
==================================================

Extend diagnostics only if necessary.

Possible additions:

GET /api/operations/active

Could return:

- active operation count
- stopping state
- current active operation metadata

Do not expose:

- secrets
- stack traces
- sensitive command arguments
- provider credentials

Only add the endpoint if it provides genuine operational value.

==================================================
SECTION 17 — TEST REQUIREMENTS
==================================================

Add automated tests.

At minimum:

### Shutdown coordinator

- stopping state changes correctly
- stopping token is cancelled
- repeated shutdown trigger is safe

### Cancellation propagation

- shutdown cancellation reaches orchestrator
- cancellation stops retry delay
- cancellation stops bulkhead wait
- caller cancellation remains Cancelled
- timeout remains Timeout

### Worker

- worker stops claiming new work
- in-flight work receives cancellation
- worker exits within configured timeout
- active operation tracking is cleaned up

### Orchestrator

- cancellation does not mark success
- cancellation stops subsequent phases
- cancellation is audited
- failure category is Cancelled

### Health

- readiness changes when shutdown starts
- liveness behavior remains correct

### API

- new operation rejected during shutdown with 503
- ProblemDetails contract remains correct
- correlation ID remains preserved

### Recovery

- stale running work can be detected
- recovery does not duplicate active work
- recovery action is auditable

==================================================
SECTION 18 — TEST QUALITY
==================================================

Tests must:

- avoid arbitrary Task.Delay
- use deterministic synchronization primitives where possible
- use CancellationTokenSource explicitly
- avoid timing-sensitive assertions
- avoid flaky concurrency tests
- not depend on execution order

Use:

- TaskCompletionSource
- ManualResetEventSlim where appropriate
- deterministic fake executors
- controllable clocks only if existing architecture supports them

Do not introduce a large test framework.

==================================================
SECTION 19 — EF CORE
==================================================

If entity changes are required:

1. Update entity configuration.
2. Generate migration.
3. Apply migration only if the repository's existing workflow normally applies local migrations.
4. Do not manually edit generated migration unless required.

==================================================
SECTION 20 — DOCUMENTATION
==================================================

Create:

.ai/context/phase-17.4.4-graceful-shutdown.md

Document:

1. Shutdown architecture
2. Cancellation propagation
3. API shutdown behavior
4. Worker shutdown behavior
5. Recovery behavior
6. Configuration
7. Audit events
8. Metrics
9. Health behavior
10. Extension points
11. Known limitations

==================================================
SECTION 21 — NON-GOALS
==================================================

Do NOT implement:

- distributed leases
- Kafka/RabbitMQ
- Kubernetes controllers
- distributed transactions
- job scheduler platform
- durable queue
- arbitrary thread termination
- automatic retry of every stale operation
- complex crash recovery engine

Keep the implementation aligned with the existing system.

==================================================
SECTION 22 — IMPLEMENTATION WORKFLOW
==================================================

Work in this order:

1. Inspect existing shutdown and cancellation flow.
2. Inspect Worker lifecycle.
3. Inspect orchestrator cancellation flow.
4. Inspect retry/timeout/bulkhead cancellation behavior.
5. Design smallest integration.
6. Implement shutdown coordination.
7. Implement stop-accepting-new-work behavior.
8. Propagate cancellation.
9. Implement worker drain behavior.
10. Implement minimal recovery.
11. Add audit events.
12. Add metrics.
13. Update health behavior.
14. Add API rejection behavior.
15. Add tests.
16. Generate migration if required.
17. Run build.
18. Run focused tests.
19. Run full test suite.
20. Fix all failures.

==================================================
SECTION 23 — REQUIRED FINAL VERIFICATION
==================================================

Run:

dotnet build apps/AI.Factory.slnx

Then:

dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s

Do not claim completion unless:

- build succeeds
- no compilation errors
- tests pass
- DI registrations are valid
- cancellation behavior is covered
- no existing Phase 17.4.1–17.4.3 behavior regressed

==================================================
FINAL REPORT
==================================================

At the end provide:

1. Summary of changes
2. Files created
3. Files modified
4. Database migrations
5. Shutdown lifecycle explanation
6. Cancellation propagation explanation
7. Worker reliability explanation
8. Recovery behavior
9. API behavior during shutdown
10. Audit events added
11. Metrics added
12. Health behavior
13. Configuration added
14. Tests added
15. Build result
16. Full test result
17. Intentionally deferred items
18. Known limitations

Do not report a feature as implemented unless it actually exists in the codebase.