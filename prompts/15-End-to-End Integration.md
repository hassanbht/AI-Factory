# Phase 15 — End-to-End Integration

You are working on the AI Factory .NET solution.

The previous implementation phases are complete. The system contains independently implemented components for:

- Context Engine
- Context Providers
- Model Routing
- Task Planning
- Agent Execution
- Autonomous Coding
- Build Execution
- Test Execution
- Validation
- Repair
- Independent Code Review
- Human Approval
- OpenViking Context Provider
- Evaluation Engine
- Learning Service

Your task is NOT to redesign or rewrite these components.

Your task is to make the existing system execute as one real end-to-end autonomous software delivery pipeline.

## Primary Goal

Ensure that a project can move through this lifecycle:

Project Created
→ Context Collected
→ Tasks Planned
→ Task Executed
→ Code Changed
→ Build Executed
→ Tests Executed
→ Validation Performed
→ Repair Attempted When Necessary
→ Independent Review Performed
→ Evaluation Performed
→ Learning Recorded
→ Human Approval Requested
→ Approved or Rejected

The implementation must use the existing abstractions wherever possible.

Do not create duplicate services.

Do not bypass existing interfaces.

Do not replace dependency injection with static service access.

---

# Step 1 — Inspect Existing Architecture

Before modifying code:

1. Read AGENTS.md.
2. Read .ai/context/architecture.md.
3. Read .ai/context/decisions.md.
4. Inspect AutonomousCodingOrchestrator.
5. Inspect all interfaces and implementations related to:

   IContextProvider
   ITaskPlanner
   IAgentExecutor
   IBuildExecutor
   ITestExecutor
   IValidationEngine
   Repair services
   Code review services
   EvaluationEngine
   LearningService
   Human approval services

6. Identify which existing components are currently disconnected from the real execution path.

Create a short internal implementation plan before changing code.

---

# Step 2 — Define the Pipeline State Machine

The orchestration flow must use explicit states.

Use or extend existing domain types where appropriate.

The pipeline should support states similar to:

Created
PreparingContext
Planning
Executing
Building
Testing
Validating
Repairing
Reviewing
Evaluating
Learning
AwaitingApproval
Approved
Rejected
Failed
Cancelled

Do not introduce unnecessary states if equivalent states already exist.

Every state transition must be persisted.

The system must be able to recover the current pipeline state after process restart.

---

# Step 3 — Integrate Context Collection

Before planning begins:

1. Collect local project context.
2. Collect historical context.
3. Collect previous failures.
4. Collect relevant learned lessons.
5. Include OpenViking context only when enabled.

The orchestration layer must depend only on IContextProvider abstractions.

OpenViking must remain optional.

If OpenViking is unavailable:

- the pipeline must continue
- fallback context providers must still work
- the failure must be logged

OpenViking must not become a single point of failure.

---

# Step 4 — Integrate Task Planning

The orchestrator must request a task plan.

The resulting plan must:

- be validated
- contain ordered tasks
- support dependencies
- contain acceptance criteria
- contain execution status
- be persisted

The pipeline must reject invalid plans before autonomous execution begins.

---

# Step 5 — Integrate Autonomous Execution

For every executable task:

1. Build execution context.
2. Select an appropriate model through IModelRouter.
3. Invoke IAgentExecutor.
4. Capture:
   - agent output
   - exit code
   - changed files
   - duration
   - errors
   - model used
   - attempt number

Do not assume agent success means implementation success.

Agent success only means the agent process completed successfully.

Actual success must be determined by the validation pipeline.

---

# Step 6 — Integrate Build/Test/Validation

After every implementation attempt:

1. Run build.
2. If build fails:
   - create validation failure context
   - enter repair loop

3. If build succeeds:
   - run tests

4. If tests fail:
   - create validation failure context
   - enter repair loop

5. If tests succeed:
   - execute validation rules

6. Continue only when validation succeeds.

The validation pipeline must return structured results.

Avoid using only strings for failure communication.

Use domain/application models for:

- BuildResult
- TestResult
- ValidationResult
- FailureContext

or existing equivalents.

---

# Step 7 — Integrate Repair Loop

RepairContext must actually be fed into the next agent prompt.

This is mandatory.

The repair prompt must contain:

- failed task
- previous implementation summary
- build errors
- test failures
- validation failures
- changed files
- previous repair attempts
- relevant learned lessons
- relevant project context

The agent must be explicitly instructed:

Do not repeat the previous failed implementation without addressing the reported failure.

Add configurable maximum repair attempts.

When maximum attempts are exceeded:

- mark the task as failed
- persist the failure
- stop autonomous retries
- continue according to the pipeline failure policy

Do not create infinite loops.

---

# Step 8 — Integrate Independent Code Review

Only after validation succeeds:

1. Select a review model independently from the implementation model when possible.
2. Perform code review.
3. Persist findings.
4. Categorize findings by severity.

Suggested severities:

Info
Warning
Error
Critical

Pipeline behavior:

Info → continue
Warning → configurable
Error → repair required
Critical → stop pipeline

Do not let the implementation agent approve its own code as the only reviewer.

---

# Step 9 — Integrate Evaluation

After successful validation and review:

Evaluate:

- task completion
- acceptance criteria
- build result
- test result
- repair count
- review findings
- execution duration
- model performance

Persist an EvaluationResult.

The evaluation must be based on structured execution evidence.

Do not generate fictional success narratives.

---

# Step 10 — Integrate Learning

After evaluation:

Generate a structured lesson only when there is useful information.

Examples:

Problem
Root Cause
Solution
Prevention
Relevant Technologies
Relevant Project Type
Model Used

The LearningService must store lessons in a structured form.

Lessons must become retrievable by the existing context providers.

Do not store:

- API keys
- secrets
- passwords
- tokens
- unnecessary raw logs

The learning system must improve future execution context.

---

# Step 11 — Human Approval

After the pipeline reaches successful completion:

Move the project or task to:

AwaitingApproval

Do not automatically mark it as Approved.

The human approval record must include:

- summary of implementation
- changed files
- build result
- test result
- validation result
- review result
- evaluation result
- repair count

Support:

Approve
Reject

When rejected:

persist the rejection reason.

The pipeline must support returning rejected work to a repair/replanning workflow according to the existing architecture.

---

# Step 12 — Failure Handling

The orchestration layer must distinguish:

AgentExecutionFailure
BuildFailure
TestFailure
ValidationFailure
RepairFailure
ReviewFailure
InfrastructureFailure
HumanRejection

Do not collapse all failures into generic exceptions.

Unexpected infrastructure exceptions may still propagate through standard exception handling.

Expected pipeline failures must be represented as structured results or domain states.

---

# Step 13 — Persistence and Recovery

All important pipeline state must survive process restart.

Persist:

- project
- tasks
- current state
- attempts
- model used
- execution results
- build results
- test results
- validation results
- review results
- evaluation results
- approval decisions

After restarting the API or Worker:

the system must be able to determine unfinished work.

Do not automatically restart potentially destructive tasks without explicit orchestration rules.

---

# Step 14 — Logging and Observability

Add structured logging for every major transition.

Every execution must have:

ExecutionId

The ExecutionId must appear in logs where applicable.

Log:

- state transitions
- task start
- task completion
- model selection
- agent execution
- build result
- test result
- validation result
- repair attempt
- review result
- evaluation result
- approval result

Never log secrets or API keys.

---

# Step 15 — Tests

Add tests for the real orchestration flow.

At minimum test:

1. Successful end-to-end execution.
2. Build failure → repair → success.
3. Test failure → repair → success.
4. Repair attempt limit exceeded.
5. Code review error → repair.
6. Evaluation is executed after success.
7. Learning is executed after evaluation.
8. Human approval is requested after successful completion.
9. Rejection is persisted.
10. OpenViking disabled → fallback context works.
11. OpenViking failure → pipeline continues using fallback context.

Tests must use fakes/mocks.

Do not call real OpenRouter, Hermes, or OpenViking APIs in unit tests.

---

# Step 16 — Do Not

Do not:

- redesign the existing architecture
- replace existing abstractions
- introduce a new workflow engine
- introduce distributed infrastructure
- introduce Kubernetes
- introduce message brokers unless already required
- add unnecessary dependencies
- implement speculative features

Keep the system practical and executable locally.

---

# Completion Criteria

The phase is complete only when:

1. dotnet build succeeds with zero errors.
2. Existing tests pass.
3. New orchestration tests pass.
4. Evaluation and Learning are actually called by the orchestration pipeline.
5. RepairContext is actually supplied to repair attempts.
6. Human approval happens only after successful pipeline completion.
7. Pipeline state is persisted.
8. The system can identify incomplete executions after restart.
9. OpenViking remains optional.
10. No existing public behavior is broken.

At the end provide:

1. Files changed.
2. Components connected.
3. Components still intentionally disconnected.
4. Any architectural assumptions.
5. Exact commands used to build and test.