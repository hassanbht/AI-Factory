# AI Factory — Phase 17.3
# Observability, Structured Logging, Audit Trail & Operational Diagnostics

You are continuing implementation of the existing AI Factory project.

Repository root:

D:\Project\AI-Factory

Before making ANY changes:

1. Read AGENTS.md completely.
2. Read all relevant files under:
   - .ai/context/
   - .ai/prompts/
3. Inspect the existing architecture and conventions.
4. Do NOT redesign completed phases.
5. Do NOT replace existing abstractions unless required.
6. Preserve backward compatibility.
7. Follow the existing Clean Architecture boundaries.
8. Do not introduce speculative abstractions.
9. Do not introduce distributed infrastructure such as Kafka, Elasticsearch, Redis, or OpenTelemetry collectors unless already present.
10. Keep this phase production-oriented but lightweight.

The system already contains completed orchestration, planning, autonomous coding, build/test/repair, review, approval, context, evaluation, and learning functionality.

The purpose of this phase is to make the system operationally diagnosable.

--------------------------------------------------
PHASE GOAL
--------------------------------------------------

Implement a lightweight but production-ready observability layer that allows operators and developers to answer:

1. What happened?
2. When did it happen?
3. Which project/task/run was involved?
4. Which agent executed the action?
5. Which model/provider was used?
6. Did it succeed or fail?
7. How long did it take?
8. What was the failure category?
9. How many retries/repair attempts occurred?
10. Who approved or rejected a decision?
11. What is the current health of the application?

The implementation must support:

- Structured logging
- Correlation IDs
- Operation/run tracing
- Persistent audit trail
- Failure diagnostics
- Operational metrics snapshots
- Health endpoints

Do NOT implement dashboards in this phase.

Do NOT implement Prometheus, Grafana, ELK, Jaeger, Kafka, or external observability infrastructure.

The architecture must allow those integrations later without coupling the domain/application layer to them.

--------------------------------------------------
SECTION 1 — INSPECT EXISTING CONVENTIONS
--------------------------------------------------

First inspect the current repository.

Identify:

- DbContext
- existing entities
- orchestration flow
- AutonomousCodingOrchestrator
- AgentExecutor
- ModelRouter
- TaskPlanner
- BuildExecutor
- TestExecutor
- ValidationEngine
- Repair flow
- Code review
- Human approval
- EvaluationEngine
- LearningService
- Context providers
- existing exception handling
- existing logging usage
- API middleware
- Worker implementation

Before implementation, determine the best integration points.

Do not create duplicate concepts if equivalent entities already exist.

--------------------------------------------------
SECTION 2 — OPERATION CORRELATION
--------------------------------------------------

Introduce a lightweight operation correlation mechanism.

Every important execution should be traceable through a single CorrelationId.

Examples:

- Project creation
- Planning operation
- Autonomous coding run
- Agent execution
- Build execution
- Test execution
- Repair attempt
- Code review
- Human approval
- Evaluation
- Learning extraction

Create an abstraction:

IOperationContext

It should expose at minimum:

- CorrelationId
- OperationId
- ParentOperationId if applicable
- ProjectId if applicable
- TaskId if applicable
- RunId if applicable

Use Guid or another existing identifier convention already used in the project.

Do not make the domain depend on ASP.NET HttpContext.

Provide an infrastructure/application implementation that works for:

- API requests
- Worker/background execution
- nested operations

For nested operations:

- child operations inherit CorrelationId
- child operation may have its own OperationId
- ParentOperationId must link to the parent operation

--------------------------------------------------
SECTION 3 — STRUCTURED LOGGING
--------------------------------------------------

Standardize structured logging.

Do not rely on string concatenation.

Logs should include structured properties when applicable:

- CorrelationId
- OperationId
- ParentOperationId
- ProjectId
- TaskId
- RunId
- AgentId or AgentName
- ModelId
- Provider
- AttemptNumber
- DurationMs
- Outcome

Create logging helpers only if they reduce duplication.

Do not create an overly complex custom logging framework.

Use Microsoft.Extensions.Logging conventions.

Important operations should log:

STARTED
SUCCEEDED
FAILED

At minimum integrate structured logging into:

1. AutonomousCodingOrchestrator
2. Task planning
3. Agent execution
4. Model routing
5. Build execution
6. Test execution
7. Validation
8. Repair loop
9. Independent code review
10. Human approval/rejection
11. Evaluation
12. Learning

Failure logs must include exceptions using normal ILogger exception logging.

Never log:

- API keys
- provider secrets
- complete private environment variables
- credentials
- tokens

--------------------------------------------------
SECTION 4 — AUDIT TRAIL
--------------------------------------------------

Implement a persistent audit trail.

This is NOT a full event sourcing system.

Create a simple append-only audit model.

Suggested entity:

AuditEvent

Fields should include:

- Id
- TimestampUtc
- CorrelationId
- OperationId
- ProjectId nullable
- TaskId nullable
- RunId nullable
- Category
- EventType
- Outcome
- ActorType
- ActorId nullable
- Summary
- MetadataJson nullable

Examples of ActorType:

System
Agent
Human

Examples of Category:

Project
Planning
Coding
Build
Test
Repair
Review
Approval
Evaluation
Learning

Examples of EventType:

Started
Completed
Failed
Approved
Rejected
Retrying
ValidationFailed
RepairStarted
RepairCompleted

Do not over-model every value if existing project conventions prefer strings.

The audit trail must be append-only from normal application usage.

Create:

IAuditService

Suggested operations:

WriteAsync(...)
GetByProjectAsync(...)
GetByCorrelationIdAsync(...)
GetRecentAsync(...)

Avoid exposing IQueryable from Application contracts.

The Application layer must not depend on EF Core.

Persistence implementation belongs in Infrastructure.

--------------------------------------------------
SECTION 5 — AUDIT EVENT WRITING
--------------------------------------------------

Integrate audit events at important lifecycle transitions.

At minimum record:

PROJECT
- created

PLANNING
- started
- completed
- failed

AUTONOMOUS CODING
- started
- completed
- failed

AGENT
- execution started
- execution completed
- execution failed

BUILD
- started
- succeeded
- failed

TEST
- started
- succeeded
- failed

VALIDATION
- succeeded
- failed

REPAIR
- started
- attempt started
- attempt completed
- attempt failed
- exhausted

REVIEW
- started
- completed
- failed

APPROVAL
- approved
- rejected

EVALUATION
- completed
- failed

LEARNING
- lesson extracted
- lesson stored
- failed

Do not write audit events for trivial internal helper calls.

The goal is operational reconstruction of important system behavior.

--------------------------------------------------
SECTION 6 — FAILURE CLASSIFICATION
--------------------------------------------------

Introduce a lightweight failure classification mechanism.

Do not create a complex exception hierarchy unless already justified.

Create a classification that can distinguish at least:

Unknown
Validation
Build
Test
Agent
Model
Provider
Timeout
Cancelled
Infrastructure
Approval
Review

Create a reusable mechanism that can classify failures from:

- exceptions
- command exit codes
- validation results
- existing domain/application failure results

Suggested abstraction:

IFailureClassifier

It should return a structured result containing:

- Category
- IsTransient
- Summary

Use conservative classification.

Do not falsely classify unknown failures.

This classifier should be used by:

- audit logging
- structured logs
- diagnostics

--------------------------------------------------
SECTION 7 — OPERATION DIAGNOSTICS
--------------------------------------------------

Create a read model/service for operational diagnostics.

Suggested abstraction:

IOperationalDiagnosticsService

Capabilities:

GetProjectSummaryAsync(projectId)

GetCorrelationSummaryAsync(correlationId)

GetRecentFailuresAsync(...)

GetRecentOperationsAsync(...)

A project diagnostic summary should answer:

- latest known status
- active/last operation
- latest task
- latest run
- last success
- last failure
- failure category
- repair attempt count
- approval status if applicable

Do not create a complex analytics engine.

Build this using existing entities plus AuditEvent data.

--------------------------------------------------
SECTION 8 — METRICS SNAPSHOT
--------------------------------------------------

Implement a lightweight in-process metrics abstraction.

Do NOT add Prometheus.

Create:

IOperationalMetrics

Track at minimum:

Counters:

- OperationsStarted
- OperationsSucceeded
- OperationsFailed
- AgentExecutions
- AgentFailures
- BuildExecutions
- BuildFailures
- TestExecutions
- TestFailures
- RepairAttempts
- RepairSuccesses
- RepairFailures
- ApprovalApproved
- ApprovalRejected

Timing metrics:

- AgentExecutionDuration
- BuildDuration
- TestDuration
- RepairDuration

Use a thread-safe implementation.

Do not persist raw metric samples.

Expose a snapshot model.

Example:

OperationalMetricsSnapshot

Include:

- generated timestamp
- counter values
- average durations where available
- total duration where available

Keep implementation simple and deterministic.

--------------------------------------------------
SECTION 9 — API OBSERVABILITY
--------------------------------------------------

Add API middleware for correlation.

Behavior:

1. Read incoming header:

X-Correlation-ID

2. If missing:
   generate a new correlation ID.

3. Store it in the operation context.

4. Return it in the response header:

X-Correlation-ID

5. Ensure logs generated during the request include the correlation ID.

Do not break existing endpoints.

--------------------------------------------------
SECTION 10 — HEALTH ENDPOINTS
--------------------------------------------------

Implement lightweight health diagnostics.

Required endpoints:

GET /health

Returns whether the API is alive.

GET /health/ready

Checks readiness of required local dependencies.

At minimum check:

- database connectivity

Do not perform expensive checks.

Optional endpoint:

GET /api/operations/metrics

Returns OperationalMetricsSnapshot.

Optional but recommended endpoints:

GET /api/operations/recent

GET /api/projects/{projectId}/diagnostics

GET /api/operations/{correlationId}

Follow existing API routing conventions.

Do not expose secrets or raw stack traces.

--------------------------------------------------
SECTION 11 — BACKGROUND WORKER SUPPORT
--------------------------------------------------

Ensure background/worker execution can create an operation context without HTTP.

Provide a factory or scope abstraction.

Suggested:

IOperationScopeFactory

Example usage:

await using var operation =
    operationScopeFactory.Begin(
        operationName,
        correlationId: existingCorrelationId);

Worker operations should:

- generate correlation IDs if missing
- propagate correlation ID into nested operations
- close/log completion
- capture duration

Do not depend on HttpContext.

--------------------------------------------------
SECTION 12 — DATABASE
--------------------------------------------------

Add EF Core persistence for AuditEvent.

Create:

- DbSet
- entity configuration
- indexes

Recommended indexes:

TimestampUtc
CorrelationId
ProjectId
TaskId
RunId
Category

Use existing migration conventions.

Do not manually modify migration history incorrectly.

Generate a proper migration.

Migration name:

AddOperationalAuditTrail

--------------------------------------------------
SECTION 13 — ERROR HANDLING
--------------------------------------------------

Do not perform a complete API error handling redesign.

However, ensure observability code itself does not hide failures.

If audit writing fails:

- log the failure
- do not silently swallow it without visibility

Do not allow audit persistence failure to recursively generate audit failures.

Avoid infinite logging/audit loops.

Use a clearly defined failure policy.

--------------------------------------------------
SECTION 14 — TESTS
--------------------------------------------------

Create meaningful tests.

Follow the existing testing conventions.

At minimum test:

Operation Context:

- creates correlation ID
- propagates correlation ID
- nested operation parent relationship

Audit:

- audit event is persisted
- filtering by project works
- filtering by correlation ID works
- ordering works

Failure classification:

- known validation failure
- build failure
- test failure
- unknown exception

Metrics:

- counters increment correctly
- failed/succeeded counters work
- timing aggregation works

Correlation middleware:

- incoming correlation ID is preserved
- missing correlation ID is generated
- response contains correlation header

Health:

- /health returns healthy
- readiness checks are correctly registered

Operational diagnostics:

- project summary aggregates audit information
- recent failures are filtered correctly

Tests must not require:

- external AI providers
- OpenRouter
- Hermes
- Docker
- internet connectivity

--------------------------------------------------
SECTION 15 — DOCUMENTATION
--------------------------------------------------

Create or update:

.ai/context/phase-17.3-observability.md

Document:

- architecture
- correlation flow
- audit event model
- failure classification
- metrics
- health endpoints
- API endpoints
- how background workers create operation scopes
- extension points

Also update architecture documentation if required.

Do not rewrite unrelated documentation.

--------------------------------------------------
SECTION 16 — DEFINITION OF DONE
--------------------------------------------------

Before finishing run:

dotnet build apps\AI.Factory.slnx

Then:

dotnet test apps\AI.Factory.slnx

Both must succeed.

Also verify:

1. API starts successfully.
2. /health works.
3. /health/ready works.
4. Correlation ID is returned by the API.
5. Audit events are persisted.
6. Diagnostics endpoint can return data.
7. Metrics endpoint returns a valid snapshot.
8. No API keys/secrets are logged.
9. No external infrastructure is required.
10. Existing functionality remains operational.

--------------------------------------------------
SECTION 17 — IMPLEMENTATION CONSTRAINTS
--------------------------------------------------

Do NOT:

- introduce microservices
- introduce message brokers
- introduce event sourcing
- introduce CQRS frameworks
- introduce MediatR solely for this phase
- introduce OpenTelemetry infrastructure
- introduce Prometheus/Grafana
- introduce Elasticsearch
- introduce Redis
- redesign the domain
- duplicate existing abstractions
- break existing tests

Prefer:

- existing conventions
- Microsoft.Extensions.Logging
- EF Core
- SQLite
- simple abstractions
- dependency injection
- deterministic tests

--------------------------------------------------
FINAL RESPONSE FORMAT
--------------------------------------------------

After implementation, provide:

1. Summary of changes
2. Files created
3. Files modified
4. Database migration created
5. New API endpoints
6. Correlation flow explanation
7. Audit model explanation
8. Metrics explanation
9. Test results
10. Build results
11. Any intentionally deferred work

If anything in the existing architecture conflicts with this specification:

DO NOT silently redesign it.

Explain the conflict and choose the smallest compatible implementation.