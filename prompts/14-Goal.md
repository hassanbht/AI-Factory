You are continuing an existing .NET 10 solution named AI.Factory.

Do not redesign or rewrite existing architecture. Inspect the existing solution and preserve all established conventions, namespaces, dependency directions, interfaces, tests, and patterns.

Current architecture already contains:

- Domain
- Application
- Infrastructure
- API
- Worker
- SQLite state persistence
- Context Engine
- ITaskPlanner contract
- ITaskPlanValidator
- IModelRouter
- IAgentExecutor
- AutonomousCodingOrchestrator
- Validation and Review abstractions
- Human Approval
- Evaluation and Learning abstractions

The current missing critical capability is a concrete AI-backed task planner.

Implement the next production-capable step:

# Goal

Create a concrete implementation of ITaskPlanner that converts a project requirement into a validated, dependency-aware structured task plan.

The implementation must be named consistently with existing architecture. Prefer a name such as ModelTaskPlanner unless an existing naming convention strongly suggests another name.

# Required behavior

The planner must:

1. Receive the project requirement and relevant context.
2. Retrieve context through the existing IContextProvider abstraction.
3. Build a planning prompt using the existing prompt infrastructure.
4. Select the appropriate planning model through IModelRouter.
5. Execute the planning request through the existing IAgentExecutor abstraction.
6. Require structured JSON output rather than free-form text.
7. Deserialize the response into the existing task planning domain/application types.
8. Validate the resulting plan using ITaskPlanValidator.
9. Validate task dependencies.
10. Detect invalid dependency graphs.
11. Detect circular dependencies.
12. Detect missing dependency references.
13. Reject malformed or incomplete plans.
14. Return useful validation errors rather than silently repairing invalid AI output.
15. Persist the validated task plan if existing persistence abstractions support this without introducing architectural violations.

# Structured output

The AI planner must produce structured JSON containing at minimum:

- task id
- title
- description
- task type
- dependencies
- acceptance criteria

Do not accept unstructured natural language as a valid task plan.

# Reliability requirements

Implement bounded retry behavior only if existing architecture supports retries.

If retrying malformed AI output:

- maximum retries must be configurable
- retries must include the validation error feedback
- retries must not loop indefinitely

Do not invent a large agent framework.

# Context

Use the existing context abstractions.

Do not directly couple the planner to:

- SQLite implementation
- OpenViking
- Hermes internals
- OpenRouter internals

All provider-specific logic must remain behind existing abstractions.

# Testing

Add unit tests for at minimum:

1. Valid plan is accepted.
2. Malformed JSON is rejected.
3. Missing required fields are rejected.
4. Missing dependency reference is rejected.
5. Circular dependency is rejected.
6. Invalid task plan is not persisted.
7. Context is passed to the planning flow.
8. Validation feedback is available for a retry attempt if retry behavior exists.

Use mocks/fakes consistent with existing test conventions.

# Important boundaries

Do not implement Build/Test execution in this change.

Do not implement OpenViking integration.

Do not redesign the Evaluation or Learning system.

Do not modify unrelated API endpoints.

Do not hardcode provider-specific model IDs in business logic.

# Deliverables

After implementation:

1. Run dotnet build on the solution.
2. Run all existing tests.
3. Add and run tests for the new planner.
4. Report:
   - files created
   - files modified
   - architectural decisions made
   - any remaining limitations
   - exact build and test results

Do not claim success unless the build and tests actually pass.