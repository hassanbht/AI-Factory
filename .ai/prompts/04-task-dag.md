# Implement AI Factory Task DAG Scheduler

Read the complete repository instructions and current implementation.

Implement a deterministic task scheduler for `WorkItem`s.

## Goal

Given a project containing WorkItems and dependencies, determine which
tasks are Ready.

Example:

```
Task A
Task B depends on A
Task C depends on B
```

Initial: `A = Ready`, `B = WaitingForDependency`, `C = WaitingForDependency`.
After A completes: `B = Ready`.
After B completes: `C = Ready`.

## Requirements

Implement:

- `ITaskScheduler`
- `ITaskDependencyResolver`
- `ITaskStateManager`

The scheduler must:

- detect completed dependencies
- detect failed dependencies
- detect circular dependencies
- select Ready tasks
- respect priority
- never execute a task twice concurrently
- persist task state
- support resume after restart

## Concurrency

Initially implement concurrency = 1. Do NOT implement parallel execution
yet. Reliability comes before performance.

## Tests

Test:

- linear dependencies
- branching dependencies
- multiple independent tasks
- circular dependency
- failed dependency
- retryable task
- already completed task
- restart/resume behavior

Run:

```
dotnet build apps/AI.Factory.slnx
dotnet test apps/AI.Factory.slnx
```

Do not integrate Hermes yet.
