# Phase 16.5 — Persistent Workflow Recovery

Implement restart-safe workflow recovery.

Requirements:

1. Review the existing execution/workflow persistence model before changing it.

2. Introduce explicit workflow lifecycle states where missing.

3. Persist enough information to determine:
   - current workflow state
   - current task
   - attempt number
   - started time
   - last heartbeat
   - last successful checkpoint
   - failure reason

4. Add a recovery service that runs when the Worker starts.

5. Recovery behavior:
   - safely identify stale/running executions
   - do not blindly rerun side-effecting operations
   - use checkpoints where available
   - mark unrecoverable executions appropriately
   - allow retry where safe

6. Add heartbeat support for long-running executions.

7. Define stale execution timeout via configuration.

8. Ensure recovery is idempotent.

9. Do not automatically rerun a task if doing so could duplicate external side effects unless an explicit idempotency mechanism exists.

10. Add tests simulating:
    - worker restart
    - stale execution
    - interrupted execution
    - recovery
    - idempotent repeated recovery

Run build and full tests.