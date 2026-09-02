# Phase 17 — Real End-to-End Runtime Integration

You are working on the AI.Factory solution.

The existing architecture and all previous phases are already implemented and tested.

Current solution:

apps/
  src/
    AI.Factory.Domain
    AI.Factory.Application
    AI.Factory.Infrastructure
    AI.Factory.Api
    AI.Factory.Worker
  tests/
    AI.Factory.Application.Tests

The solution currently builds successfully and all existing tests pass.

Your task is NOT to redesign the existing architecture.

Your task is to implement a real end-to-end execution path using the existing abstractions.

## Primary Goal

A real coding request must be able to travel through the complete orchestration pipeline:

Request
→ Project/Task
→ Context retrieval
→ Task planning
→ Model selection
→ Agent execution
→ Workspace changes
→ Build
→ Test
→ Repair if necessary
→ Independent review
→ Evaluation
→ Learning
→ Approval state
→ Final execution result

The implementation must reuse existing interfaces and services wherever possible.

Do not create parallel abstractions for functionality that already exists.

--------------------------------------------------

# 1. Inspect Existing Architecture First

Before making changes:

1. Inspect all projects.
2. Inspect the existing Domain entities.
3. Inspect Application interfaces.
4. Inspect Infrastructure implementations.
5. Inspect AutonomousCodingOrchestrator.
6. Inspect ITaskPlanner.
7. Inspect IAgentExecutor.
8. Inspect IModelRouter.
9. Inspect context provider abstractions.
10. Inspect build/test/validation abstractions.
11. Inspect repair abstractions.
12. Inspect code review abstractions.
13. Inspect EvaluationEngine.
14. Inspect LearningService.
15. Inspect Human Approval implementation.
16. Inspect Worker project.

Do not duplicate existing functionality.

Write the implementation around the existing architecture.

--------------------------------------------------

# 2. Introduce a Real Orchestration Run

Create or complete a central runtime concept representing one complete execution.

Use an existing equivalent if already present.

If no equivalent exists, introduce:

OrchestrationRun

Suggested properties:

Id
ProjectId
TaskId
Status
RequestedAt
StartedAt
CompletedAt
CurrentStage
Attempt
MaxAttempts
SelectedModel
WorkspacePath
ErrorMessage

Statuses should represent the actual lifecycle.

Suggested states:

Pending
Running
Planning
Executing
Building
Testing
Repairing
Reviewing
Evaluating
AwaitingApproval
Completed
Failed
Cancelled

Do not introduce unnecessary complexity.

--------------------------------------------------

# 3. Create a Single End-to-End Entry Point

Create one application-level service responsible for starting an execution.

Suggested abstraction:

IOrchestrationService

Suggested method:

Task<OrchestrationRunResult> StartAsync(
    StartOrchestrationCommand command,
    CancellationToken cancellationToken);

The API must not directly coordinate all subsystems.

The orchestration service must be the single entry point.

--------------------------------------------------

# 4. Implement the Real Pipeline

The execution flow must be:

STEP 1
Load Project and Task.

STEP 2
Create an orchestration run.

STEP 3
Retrieve relevant context using existing IContextProvider abstractions.

STEP 4
Plan the work using ITaskPlanner.

STEP 5
Validate the generated plan.

If invalid:

mark the run as Failed.

Do not call Hermes.

STEP 6
Select the model using IModelRouter.

STEP 7
Execute the task through IAgentExecutor.

The agent must receive:

task description
task plan
retrieved context
workspace path
model selection
execution constraints

STEP 8
Persist the agent execution result.

STEP 9
Run the existing build executor.

STEP 10
Run the existing test executor.

STEP 11
Run validation.

If build and tests succeed:

continue.

If they fail:

continue to repair.

STEP 12
Build RepairContext from the actual failure.

STEP 13
Feed RepairContext into the next agent execution.

This is important.

Do not only log RepairContext.

It must become part of the next execution prompt.

STEP 14
Repeat:

Agent Execution
→ Build
→ Test
→ Validation

until:

Success

or

MaxAttempts reached.

STEP 15
Run independent code review.

STEP 16
Run evaluation.

STEP 17
Extract learning data using existing LearningService abstractions.

STEP 18
Persist the completed result.

STEP 19
Move to Human Approval when approval is required.

Otherwise mark Completed.

--------------------------------------------------

# 5. Attempt Handling

The system must never retry indefinitely.

Use a configurable maximum attempt count.

Example:

Orchestration:
  MaxAttempts: 3

Each attempt must be persisted.

At minimum store:

AttemptNumber
StartedAt
CompletedAt
AgentResult
BuildResult
TestResult
ValidationResult
RepairContext
ReviewResult

Do not overwrite previous attempts.

--------------------------------------------------

# 6. Failure Classification

Failures must be classified.

Suggested categories:

PlanningFailure
AgentFailure
BuildFailure
TestFailure
ValidationFailure
ReviewFailure
InfrastructureFailure
Timeout
Cancelled

Do not depend only on exception text.

Use structured failure data where possible.

--------------------------------------------------

# 7. Workspace Safety

The orchestrator must never allow arbitrary host paths from an API request.

Workspace paths must be validated.

Introduce a configured WorkspaceRoot.

Example:

D:\Project\AI-Factory\sandbox\agent-workspace

Every execution workspace must be inside WorkspaceRoot.

Suggested layout:

sandbox/
  runs/
    {runId}/
      workspace/

The API caller must never directly provide an arbitrary absolute filesystem path.

--------------------------------------------------

# 8. Workspace Preparation

Before agent execution:

Create an isolated workspace for the run.

Possible approaches:

Copy the source project.

or

Create a Git worktree if Git integration already exists.

Prefer the simplest existing mechanism.

Do not allow one run to overwrite another run's workspace.

--------------------------------------------------

# 9. Worker Integration

Use AI.Factory.Worker for asynchronous orchestration.

The API should:

1. Create the run.
2. Persist it.
3. Queue the run.
4. Return immediately.

The Worker should:

1. Retrieve pending runs.
2. Mark the run Running.
3. Execute the orchestration.
4. Persist progress.
5. Handle failure safely.

Do not run long Hermes operations inside the HTTP request.

--------------------------------------------------

# 10. API Endpoints

Implement minimal operational endpoints.

POST

/api/orchestration-runs

Starts a run.

Request example:

{
  "projectId": "...",
  "taskId": "...",
  "maxAttempts": 3
}

Return:

202 Accepted

with:

runId
status
statusUrl

--------------------------------------------------

GET

/api/orchestration-runs/{id}

Returns:

run metadata
current status
current stage
attempt count
selected model
timestamps
error summary

Do not expose secrets.

--------------------------------------------------

GET

/api/orchestration-runs/{id}/attempts

Returns execution history.

--------------------------------------------------

GET

/api/orchestration-runs/{id}/logs

Returns structured logs.

--------------------------------------------------

POST

/api/orchestration-runs/{id}/cancel

Requests cancellation.

Cancellation must propagate through:

Worker
Agent execution
Build
Test

Use CancellationToken where supported.

--------------------------------------------------

# 11. Real-Time Progress

Implement structured progress events.

Suggested interface:

IOrchestrationProgressReporter

Example stages:

RunCreated
ContextRetrieved
PlanningStarted
PlanReady
ModelSelected
AgentStarted
AgentCompleted
BuildStarted
BuildCompleted
TestsStarted
TestsCompleted
RepairStarted
RepairCompleted
ReviewStarted
ReviewCompleted
EvaluationCompleted
LearningCompleted
AwaitingApproval
Completed
Failed

Persist progress events.

Do not rely only on console logs.

--------------------------------------------------

# 12. Hermes Integration

Use the existing Hermes implementation.

Do not create a fake executor.

Verify that:

workspace path is mounted correctly.

the agent command can access the workspace.

execution timeout is configurable.

stdout and stderr are captured.

exit code is persisted.

The orchestrator must distinguish:

successful agent execution

from

successful build/test validation.

An agent returning exit code 0 does not mean the task succeeded.

--------------------------------------------------

# 13. Configuration

Add configuration sections without hardcoded paths.

Example:

Orchestration:
  WorkspaceRoot
  MaxAttempts
  QueuePollingInterval
  ExecutionTimeout

Hermes:
  Enabled
  TimeoutSeconds

Worker:
  MaxConcurrentRuns

Use options binding and validation.

Fail fast on invalid configuration.

--------------------------------------------------

# 14. Concurrency

Prevent multiple workers from executing the same run.

Use database-safe claiming.

Suggested approach:

Pending
→ Claimed
→ Running

The update must be concurrency-safe.

A run must not be processed twice.

--------------------------------------------------

# 15. Crash Recovery

Handle interrupted runs.

If Worker stops during execution:

runs must not remain permanently Running.

Introduce a recoverable state or lease.

Suggested mechanism:

WorkerHeartbeat
LeaseExpiresAt

A stale run may be recovered safely.

Do not automatically rerun a task without checking the previous state.

--------------------------------------------------

# 16. Evaluation and Learning Integration

Use the existing EvaluationEngine and LearningService.

After a completed execution:

EvaluationEngine must receive:

task
plan
agent result
build result
test result
review result
attempt count

LearningService must receive structured execution data.

Do not invent an unstructured natural-language lesson extraction system.

Persist structured lessons based on actual failures and successful repairs.

--------------------------------------------------

# 17. Human Approval Integration

If approval is required:

the run must stop before final completion.

Status:

AwaitingApproval

Approval:

→ Completed

Rejection:

→ Rejected or Failed

Do not continue agent execution after human rejection.

--------------------------------------------------

# 18. Error Handling

Add proper exception handling for the new orchestration endpoints.

Do not expose internal stack traces.

Use appropriate HTTP status codes.

At minimum:

400 Invalid request

404 Run not found

409 Invalid state transition

500 Unexpected error

Add a global exception handling mechanism only if it improves the existing API consistently.

Avoid endpoint-specific try/catch duplication.

--------------------------------------------------

# 19. Observability

Every orchestration run must have a correlation ID.

Include:

RunId
ProjectId
TaskId
AttemptNumber

in structured logs.

Do not log:

API keys
authorization tokens
model secrets
full environment variables

--------------------------------------------------

# 20. Tests

Add tests for the complete pipeline.

At minimum:

1. Successful execution

Plan
→ Agent
→ Build
→ Test
→ Review
→ Evaluation
→ Completed

2. Build failure followed by successful repair.

3. Test failure followed by successful repair.

4. Max attempts exceeded.

5. Invalid plan.

6. Agent failure.

7. Cancellation.

8. Human approval required.

9. Human rejection.

10. Duplicate worker execution prevention.

11. Workspace path validation.

12. Stale run recovery.

13. API returns 202 immediately.

14. API status endpoint.

15. RepairContext is included in the next agent attempt.

The tests must not require a real Hermes server.

Use test doubles for end-to-end application tests.

Keep a separate integration test boundary for real Hermes.

--------------------------------------------------

# 21. Real Hermes Smoke Test

Add a separate optional smoke test mechanism.

It must be disabled by default.

Example configuration:

IntegrationTests:
  HermesEnabled: false

When enabled:

Use a small controlled task.

Example:

Create a text file named hello.txt containing:

Hello AI Factory

Then verify:

file exists

content matches

Do not run this automatically in normal CI.

--------------------------------------------------

# 22. Completion Criteria

Before finishing:

Run:

dotnet build apps\AI.Factory.slnx

dotnet test apps\AI.Factory.slnx

All tests must pass.

Then provide:

1. Files added.
2. Files modified.
3. Architecture decisions.
4. Pipeline execution flow.
5. Configuration required.
6. API endpoints.
7. How to run API.
8. How to run Worker.
9. How to submit a real orchestration request.
10. How to inspect progress.
11. Known limitations.

Do not leave placeholder implementations.

Do not fabricate successful agent execution.

Do not replace existing abstractions unnecessarily.

The goal is a genuinely runnable vertical slice.