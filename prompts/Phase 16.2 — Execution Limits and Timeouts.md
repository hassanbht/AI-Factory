# Phase 16.2 — Execution Limits and Timeouts

Implement centralized execution limits for AI.Factory.

Requirements:

1. Add strongly typed ExecutionLimits configuration.

2. Configuration must include:

   ModelRequestTimeoutSeconds
   AgentExecutionTimeoutMinutes
   BuildTimeoutMinutes
   TestTimeoutMinutes
   MaxRepairAttempts
   MaxWorkflowDurationMinutes

3. Bind configuration through Options pattern.

4. Apply CancellationToken and timeout enforcement to:

   IModelRouter
   IAgentExecutor
   build execution
   test execution
   repair loop
   AutonomousCodingOrchestrator

5. A timeout must result in a clear execution failure state, not an unhandled exception.

6. Preserve cancellation semantics:
   - user cancellation must not be treated as an infrastructure failure
   - timeout must be distinguishable from manual cancellation

7. Log timeout metadata:
   - execution id
   - operation type
   - configured limit
   - actual elapsed duration

8. Add unit tests for:
   - timeout
   - cancellation
   - timeout classification
   - configuration validation

Do not hardcode timeout values outside configuration.
Run build and full tests.