# Phase 17.2 — Observability and Execution Traceability

You are continuing work on the existing AI Factory repository.

Repository root:

D:\Project\AI-Factory

The system is a .NET 10 solution using Clean Architecture.

Current projects:

apps/src/AI.Factory.Domain
apps/src/AI.Factory.Application
apps/src/AI.Factory.Infrastructure
apps/src/AI.Factory.Api
apps/src/AI.Factory.Worker
apps/tests/AI.Factory.Application.Tests

The solution already contains autonomous orchestration capabilities including:

- Project management
- Context engine
- Task planning contracts
- Model routing
- Agent execution
- Autonomous coding orchestration
- Build/test/repair loop
- Independent code review
- Human approval
- OpenViking context abstraction
- Evaluation
- Learning
- Production hardening foundation

Do not redesign the architecture.

Do not replace existing abstractions.

Do not introduce a second orchestration system.

The goal of this phase is to make autonomous executions observable and traceable.

==================================================
GOAL
==================================================

Implement persistent execution tracing.

For every autonomous execution, the system must record:

- Execution identity
- Related project
- Related task
- Overall execution status
- Start and completion timestamps
- Execution duration
- Individual execution steps
- Step status
- Step duration
- Model/provider information when available
- Agent information when available
- Failure information

The system must allow querying execution history through the API.

==================================================
ARCHITECTURAL REQUIREMENTS
==================================================

Follow the existing Clean Architecture.

Domain:

- entities
- enums
- value objects if useful

Application:

- interfaces
- DTOs
- services
- use cases

Infrastructure:

- EF Core persistence
- repository implementations
- execution persistence

API:

- HTTP endpoints only
- request/response DTO mapping

Do not put persistence logic in Application.

Do not put orchestration logic in API controllers/endpoints.

==================================================
DOMAIN MODEL
==================================================

Create an Execution entity.

Suggested properties:

Id
ProjectId
TaskId (nullable if execution is project-level)
Status
StartedAtUtc
CompletedAtUtc
FailureMessage
FailureType

Execution status should support at minimum:

Pending
Running
Succeeded
Failed
Cancelled

Create an ExecutionStep entity.

Suggested properties:

Id
ExecutionId
Name
Status
StartedAtUtc
CompletedAtUtc
FailureMessage
Metadata

Step status should support:

Pending
Running
Succeeded
Failed
Skipped

Metadata should be stored in a simple format compatible with SQLite.

Do not prematurely introduce complex JSON infrastructure.

A nullable string field containing serialized JSON is acceptable.

Execution must own or aggregate its steps consistently with existing Domain conventions.

==================================================
APPLICATION CONTRACTS
==================================================

Create an abstraction for execution tracking.

Suggested interface:

IExecutionTracker

It must support operations conceptually equivalent to:

StartExecutionAsync
CompleteExecutionAsync
FailExecutionAsync
StartStepAsync
CompleteStepAsync
FailStepAsync

Do not force the exact method names if existing naming conventions suggest better alternatives.

The abstraction must allow orchestration code to record progress without depending on EF Core.

Create query capabilities for:

- list executions
- get execution by ID
- list execution steps

Use DTOs for API-facing data.

==================================================
ORCHESTRATION INTEGRATION
==================================================

Integrate execution tracking into the existing AutonomousCodingOrchestrator.

Do not rewrite the orchestrator.

Instrument the existing execution flow.

At minimum, trace these logical phases when they occur:

Planning
Context
Coding
Build
Test
Repair
Review
HumanApproval

Not every execution must execute every step.

If a step is skipped, record it as Skipped when the system can determine that explicitly.

If the step does not occur at all because execution ends earlier, do not fabricate successful data.

Execution tracking must never silently hide an orchestration failure.

If orchestration fails:

1. mark the current step as Failed
2. capture the failure message
3. mark the execution as Failed
4. preserve the original exception behavior unless existing error handling explicitly handles it

Do not swallow exceptions merely for observability.

==================================================
MODEL AND AGENT METADATA
==================================================

When information is available from existing abstractions, record metadata such as:

provider
model
agent
attempt number

Do not modify interfaces across the entire system solely to add metadata.

Use existing data flow.

If metadata is not available, leave it null rather than inventing it.

==================================================
PERSISTENCE
==================================================

Persist executions and steps using the existing EF Core SQLite infrastructure.

Add:

Execution configuration
ExecutionStep configuration
DbSets
relationships
indexes

Create indexes appropriate for:

ProjectId
TaskId
Status
StartedAtUtc

Create and apply EF Core migrations according to the existing project conventions.

Do not introduce a second database.

Do not introduce PostgreSQL, Redis, OpenTelemetry storage, Elasticsearch, or external observability infrastructure.

This phase must remain locally runnable with SQLite.

==================================================
API
==================================================

Implement endpoints equivalent to:

GET /api/executions

GET /api/executions/{id}

GET /api/projects/{projectId}/executions

The list endpoint should support sensible pagination.

Suggested parameters:

page
pageSize

The response should include enough information for a future UI.

Execution detail should include:

execution metadata
status
timestamps
failure information
ordered execution steps

Do not expose EF entities directly.

==================================================
ORDERING
==================================================

Execution steps must be returned in execution order.

Use StartedAtUtc as the primary ordering signal unless an explicit sequence is required by the existing architecture.

Do not create nondeterministic ordering.

==================================================
ERROR HANDLING
==================================================

Follow the global exception/error-handling conventions introduced in the previous production-hardening phase.

Do not introduce inconsistent per-endpoint exception handling.

Not found executions should follow the existing API conventions.

==================================================
TESTING
==================================================

Add tests for:

1. execution creation
2. execution completion
3. execution failure
4. step creation
5. step completion
6. step failure
7. ordering of steps
8. query by execution ID
9. query by project ID
10. orchestration integration
11. failed orchestration correctly records failed step and execution

Use the existing test project and conventions.

Do not introduce a new test framework.

==================================================
NON-GOALS
==================================================

Do not implement:

- Grafana
- Prometheus
- Jaeger
- distributed tracing
- OpenTelemetry exporters
- external log aggregation
- Kubernetes
- message queues
- real-time SignalR dashboards

Those are not required yet.

This phase is about persistent internal execution traceability.

==================================================
IMPLEMENTATION PROCESS
==================================================

Before modifying code:

1. Inspect the existing repository.
2. Inspect the existing AutonomousCodingOrchestrator.
3. Inspect existing EF Core DbContext and configurations.
4. Inspect existing exception handling.
5. Inspect existing Application service conventions.
6. Inspect existing tests.

Then implement the smallest coherent solution consistent with the repository.

Do not ask questions unless the repository genuinely contains conflicting architectural constraints.

When finished:

1. Run dotnet build on the solution.
2. Run dotnet test on the solution.
3. Fix all failures.
4. Inspect migrations.
5. Verify API compilation.
6. Provide a concise summary containing:
   - files changed
   - domain additions
   - application additions
   - infrastructure additions
   - API endpoints
   - tests added
   - migrations created
   - build result
   - test result
   - intentionally deferred items

Do not leave placeholder implementations.

Do not fabricate execution data.

The system must record real execution events produced by the existing orchestration flow.