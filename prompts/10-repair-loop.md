# Implement Autonomous Coding Orchestrator

Implement the end-to-end task execution loop.

The loop is:

Ready Task
→ Build Context
→ Select Agent
→ Select Model
→ Execute Agent
→ Capture Result
→ Persist AgentRun
→ Detect Changed Files
→ Build
→ Test
→ Evaluate
→ Complete or Repair

Requirements:

1. Only execute Ready tasks.
2. Lock the task before execution.
3. Prevent duplicate concurrent execution.
4. Increment AttemptCount.
5. Persist AgentRun before execution.
6. Execute through IAgentExecutor.
7. Persist output.
8. Run validation.
9. On success mark task Completed.
10. On failure create a Repair task or transition to Repairing.
11. Respect MaxAttempts.
12. Never retry indefinitely.
13. Persist every state transition.
14. Recover unfinished tasks after process restart.

The orchestrator must be resumable.

It must not depend on in-memory state for correctness.

Implement structured logging.

Create integration tests using fake IAgentExecutor.

Do not call real LLMs from unit tests.

Do not use real production repositories for tests.

Run:

dotnet build apps/AI.Factory.slnx

dotnet test apps/AI.Factory.slnx
