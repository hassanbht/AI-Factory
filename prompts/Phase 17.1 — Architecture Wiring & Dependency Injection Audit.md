# AI Factory — Phase 17.1
# Architecture Wiring & Dependency Injection Audit

You are working inside the existing `AI-Factory` repository.

Repository root:

`D:\Project\AI-Factory`

Solution:

`apps\AI.Factory.slnx`

The solution currently builds successfully and all existing tests pass.

Current baseline:

```text
dotnet build apps\AI.Factory.slnx
SUCCESS

dotnet test apps\AI.Factory.slnx
SUCCESS
165 tests passed
0 failed
```

Your task in this phase is **NOT to redesign the architecture and NOT to add new product features**.

Your task is to perform a complete and evidence-based audit of the existing architecture wiring and then fix only the missing or incorrect wiring required for the existing architecture to execute correctly.

The main objective is:

> Verify that all previously implemented components are actually connected through Dependency Injection and are reachable from the real orchestration flow.

---

# 1. IMPORTANT SCOPE

Before modifying any code:

1. Read the repository structure.
2. Read `AGENTS.md` if it exists.
3. Read all architecture documentation under `.ai/context/`.
4. Read all implementation prompts under `.ai/prompts/` that describe the existing phases.
5. Inspect the existing Application, Infrastructure, API and Worker projects.
6. Inspect the current composition roots and Dependency Injection registrations.
7. Inspect all existing orchestrators and runtime entry points.

Do not assume that an implementation is missing simply because an interface is not referenced in one file.

First establish the actual architecture.

---

# 2. PRIMARY GOAL

Produce an evidence-based wiring audit for the following pipeline:

```text
Project
    ↓
Context Engine
    ↓
Task Planner
    ↓
Model Router
    ↓
Agent Executor
    ↓
Autonomous Coding Orchestrator
    ↓
Build
    ↓
Test
    ↓
Validation
    ↓
Repair
    ↓
Independent Code Review
    ↓
Human Approval
    ↓
Evaluation
    ↓
Learning
```

Determine for every existing component:

1. Does its interface exist?
2. Does a concrete implementation exist?
3. Is it registered in Dependency Injection?
4. Can it be resolved at runtime?
5. Is it actually consumed by another component?
6. Is it reachable from the real execution path?
7. Is it currently dead code?
8. Is it intentionally deferred according to architecture documentation?

Do not treat intentionally deferred components as bugs.

---

# 3. REQUIRED AUDIT MATRIX

Create an internal audit matrix for all discovered architectural components.

At minimum inspect these abstractions if they exist:

```text
IModelRouter
IAgentExecutor
ITaskPlanner
IContextProvider
IBuildExecutor
ITestExecutor
IValidationEngine
ICodeReviewer
IEvaluationEngine
ILearningService
IAutonomousCodingOrchestrator
IProjectService
ITaskService
IApprovalService
```

Also inspect any equivalent abstractions that use different names.

For every component determine:

```text
Interface:
Implementation:
Assembly:
DI Registration:
Lifetime:
Direct Consumers:
Runtime Entry Point:
Reachable From API:
Reachable From Worker:
Currently Used:
Status:
```

Allowed status values:

```text
WIRED
PARTIALLY_WIRED
IMPLEMENTED_NOT_REGISTERED
REGISTERED_NOT_USED
DEAD_CODE
INTENTIONALLY_DEFERRED
MISSING_IMPLEMENTATION
```

Do not create new interfaces just to make the matrix look complete.

---

# 4. COMPOSITION ROOT AUDIT

Inspect all composition roots.

At minimum inspect:

```text
AI.Factory.Api/Program.cs
AI.Factory.Worker/Program.cs
```

Also inspect extension methods such as:

```text
AddApplication(...)
AddInfrastructure(...)
AddPersistence(...)
AddServices(...)
```

or equivalent.

Determine:

1. Which project owns Dependency Injection registrations.
2. Whether Infrastructure implementations are registered.
3. Whether Application services are registered.
4. Whether singleton/scoped/transient lifetimes are appropriate.
5. Whether hosted services are registered correctly.
6. Whether runtime dependencies can actually be resolved.

Do not introduce a new DI architecture unless the existing architecture is broken.

Prefer extending the existing registration pattern.

---

# 5. ORCHESTRATOR TRACE

Find the real implementation responsible for autonomous execution.

It may be named something similar to:

```text
AutonomousCodingOrchestrator
CodingOrchestrator
AgentOrchestrator
ExecutionOrchestrator
```

Trace its execution path from beginning to end.

For every dependency injected into the orchestrator determine:

```text
Where does this dependency come from?
Is it registered in DI?
Is it actually called?
What happens with its result?
Does its result influence the next stage?
```

Specifically investigate whether these architectural components are merely implemented but disconnected:

```text
Context Engine
Task Planner
Model Router
Agent Executor
Build Executor
Test Executor
Validation Engine
Repair Context
Code Reviewer
Human Approval
Evaluation Engine
Learning Service
```

---

# 6. DEAD IMPLEMENTATION DETECTION

Search for implementations that are complete but never used.

Examples:

```text
Concrete service exists
+
Registered in DI
+
No consumer exists
```

or:

```text
Concrete service exists
+
Not registered
```

or:

```text
Service is injected
+
But its result is ignored
```

or:

```text
Service generates context
+
Context never reaches the next AI prompt
```

or:

```text
Evaluation is implemented
+
But no completed execution invokes it
```

For every such case determine whether this is:

```text
A real wiring defect
or
An intentional phase boundary
```

Use `.ai/context/architecture.md` and `.ai/context/decisions.md` as the primary source of truth.

Do not silently change an intentional architectural boundary.

---

# 7. DI VALIDATION

Add automated validation for the real service registrations.

The preferred outcome is that important runtime composition roots can be tested.

At minimum add tests capable of detecting:

```text
Required service is not registered
Required implementation cannot be resolved
Constructor dependencies cannot be resolved
Incorrect registration breaks orchestration
```

Do not create an enormous test suite.

Keep the tests focused.

Preferred location:

```text
apps/tests/AI.Factory.Application.Tests
```

or create/use an existing infrastructure/integration test project if one already exists and is architecturally appropriate.

Do not move existing tests unnecessarily.

---

# 8. WORKER AND API AUDIT

Determine how execution is supposed to start.

Inspect whether:

```text
API
```

can trigger:

```text
Project → Task → Execution
```

and whether:

```text
Worker
```

can actually execute the orchestration pipeline.

Important:

Do not implement the full Phase 17 Worker architecture if it does not already exist.

This phase only answers:

> Are the existing execution components connected to an actual runtime entry point?

If the answer is no, determine the smallest wiring change consistent with the current architecture.

Do not invent a queue, Redis, RabbitMQ, Hangfire, Quartz, or another background architecture.

---

# 9. MODEL AND AGENT EXECUTION AUDIT

The system already has an intended model and agent architecture.

Inspect the actual implementations.

Verify:

```text
IModelRouter
    ↓
model selection
    ↓
IAgentExecutor
    ↓
Hermes or configured agent execution
```

Determine whether:

1. The router can actually return a model configuration.
2. The selected model is passed to the executor if the architecture expects it.
3. The executor is reachable from orchestration.
4. Hermes integration is actually invoked.
5. The workspace path is propagated correctly.
6. Failures are returned to the orchestration layer.
7. No fake implementation is accidentally used in production DI.

Do not replace the existing Hermes integration.

Do not change provider configuration unless required to fix an actual broken connection.

---

# 10. CONTEXT FLOW AUDIT

Trace context from generation to consumption.

Determine whether:

```text
Context Provider
    ↓
Context Engine
    ↓
Task Planner / Agent Prompt
```

is actually connected.

Specifically check:

```text
LocalContextProvider
OpenVikingContextProvider
ContextCategory
TestFailures
RepairContext
```

For each component determine whether its current disconnected state is intentional.

Important:

The known architecture previously stated that some sources may legitimately return empty values until later phases.

Do not fabricate a data source for `TestFailures`.

Do not force OpenViking to be enabled.

If:

```text
OpenVikingContextProvider.Enabled == false
```

then this must remain valid.

Do not introduce a fake OpenViking endpoint.

---

# 11. REPAIR FLOW AUDIT

Inspect the complete failure path.

Trace:

```text
Agent Execution
    ↓
Build Failure
or
Test Failure
or
Validation Failure
    ↓
Repair Context
    ↓
Next Attempt
```

Determine whether `RepairContext` is:

```text
Generated
Stored
Logged
Consumed
```

If the architecture documentation says it should now influence the next attempt, wire it.

If the documentation explicitly leaves that integration for a later phase, do not change it.

Do not infer a new prompt design unless existing prompt infrastructure already supports the flow.

---

# 12. EVALUATION AND LEARNING AUDIT

Inspect:

```text
EvaluationEngine
LearningService
```

Determine:

1. Are they implemented?
2. Are they registered?
3. Are they reachable?
4. Are they invoked after successful or failed execution?
5. Is their current disconnection intentional according to documentation?

Do not invent a "lesson extraction" mechanism.

Do not invent a new schema for converting tasks into lessons.

If existing architecture provides a defined integration point, use it.

Otherwise document the missing boundary without implementing speculative behavior.

---

# 13. HUMAN APPROVAL AUDIT

Inspect approval flow.

Verify:

```text
Review Result
    ↓
Approval Requirement
    ↓
Human Decision
    ↓
Execution Continues / Fails / Completes
```

Determine whether approval endpoints are connected to the orchestration lifecycle.

Do not add new HTTP exception handling conventions unless required for an actual runtime wiring failure.

Do not introduce global exception middleware in this phase.

---

# 14. IMPLEMENTATION RULES

After completing the audit:

Fix only defects classified as:

```text
IMPLEMENTED_NOT_REGISTERED
REGISTERED_NOT_USED
PARTIALLY_WIRED
```

when the existing architecture documents that the component should already participate in the current runtime flow.

Do not implement:

```text
New product features
New phases
New infrastructure
New databases
New queues
New external services
New AI providers
New architectural patterns
```

Do not refactor working components simply for style.

Prefer minimal changes.

---

# 15. REQUIRED TESTS

After making changes, ensure:

```text
dotnet build apps\AI.Factory.slnx
```

passes.

Then:

```text
dotnet test apps\AI.Factory.slnx
```

passes.

Add targeted tests for every wiring defect you fix.

Examples:

```text
Service registration test
Orchestrator dependency resolution test
Context reaches intended consumer test
Model router reaches agent executor test
Existing execution path invokes expected component test
```

Do not mock the entire architecture just to test DI.

Tests should verify meaningful composition.

---

# 16. NO SILENT ARCHITECTURAL CHANGES

If you encounter ambiguity:

Do not silently invent architecture.

Use this priority order:

```text
1. Existing implementation
2. Existing tests
3. .ai/context/architecture.md
4. .ai/context/decisions.md
5. Existing phase prompts
6. Existing naming and dependency patterns
```

Only after these sources are exhausted should you make a minimal inference.

Explicitly document that inference.

---

# 17. REQUIRED FINAL REPORT

At the end provide a detailed report containing:

## A. Audit Matrix

For every major component:

```text
Component
Interface
Implementation
DI Registered
Runtime Reachable
Consumer
Status
Action Taken
```

## B. Wiring Defects Found

For each defect:

```text
Problem
Why it was a problem
Affected execution path
Fix
Files changed
Test added
```

## C. Intentionally Deferred Components

List components that remain disconnected because the architecture explicitly defers them.

Explain why they were not connected.

## D. Runtime Execution Trace

Provide the actual execution path after this phase:

```text
API / Worker
    ↓
...
    ↓
Final Result
```

This must reflect real code, not intended architecture.

## E. Files Changed

List every modified file.

## F. Validation

Run and report:

```text
dotnet build apps\AI.Factory.slnx
dotnet test apps\AI.Factory.slnx
```

Include the final result and test count.

---

# 18. DEFINITION OF DONE

This phase is complete only when:

```text
✓ Existing architecture has been fully audited
✓ Major interfaces have been mapped to implementations
✓ DI registrations have been verified
✓ Real orchestration dependencies can resolve
✓ Components expected to participate are actually reachable
✓ Dead/disconnected implementations are identified
✓ Intentional phase boundaries are preserved
✓ Minimal wiring defects are fixed
✓ Targeted tests exist for fixed wiring
✓ Solution builds successfully
✓ All tests pass
✓ Final report distinguishes REAL wiring from intended architecture
```

The goal is not to maximize the number of components used.

The goal is:

> Make the currently completed architecture actually connected and executable, while preserving intentional boundaries and avoiding speculative implementation.

Start by auditing the repository before changing any code.