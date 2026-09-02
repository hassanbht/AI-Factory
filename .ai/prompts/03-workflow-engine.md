# Implement AI Factory Workflow Engine

You are implementing the core orchestration engine.

Read:

- `AGENTS.md`
- `CLAUDE.md`
- `.ai/context/*`
- `.ai/state/implementation-status.md`

First inspect the existing implementation. Do not rewrite the existing
domain model unless necessary.

## Goal

Implement a reliable state-machine based workflow engine. The workflow
must support:

```
Created
→ Analyzing
→ Specification
→ Architecture
→ Planning
→ Implementation
→ Testing
→ Repairing
→ Reviewing
→ HumanReview
→ Completed
```

Failure: any state → `Failed`.
Cancellation: any non-terminal state → `Cancelled`.

## Requirements

Create abstractions:

- `IWorkflowEngine`
- `IWorkflowStateStore`
- `IWorkflowTransitionValidator`

The engine must:

1. load project state;
2. determine current state;
3. validate transition;
4. persist transition;
5. record timestamp;
6. record reason;
7. record actor;
8. make transitions auditable.

Do not allow arbitrary state transitions. Invalid transitions must fail
explicitly.

The engine must be resumable after process restart. Do not keep
authoritative workflow state only in memory.

## Add transition history

Create a `WorkflowTransition` entity containing:

- `Id`
- `ProjectId`
- `FromStatus`
- `ToStatus`
- `Reason`
- `Actor`
- `CreatedAtUtc`

Persist it using EF Core.

## Testing

Create unit tests for:

- valid transitions
- invalid transitions
- terminal states
- cancellation
- failure
- resume after persistence

Run:

```
dotnet build apps/AI.Factory.slnx
dotnet test apps/AI.Factory.slnx
```

Do not implement Hermes integration yet.
Do not implement model routing yet.
Do not implement OpenViking yet.
Do not implement autonomous coding yet.

The workflow engine must be deterministic and testable without an LLM.

Update `implementation-status.md` after validation.
