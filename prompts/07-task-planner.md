# Implement AI Factory Task Planner

Implement a task planning service that transforms an approved software specification into an executable WorkItem graph.

Create:

ITaskPlanner

TaskPlanRequest

TaskPlan

PlannedTask

The planner must produce:

* task ID
* title
* description
* type
* priority
* dependencies
* acceptance criteria
* estimated complexity

The planner must NOT directly modify the database.

It returns a plan.

A separate application service persists the approved plan.

The output must be deterministic in structure even if the underlying AI provider is non-deterministic.

Validate:

* no duplicate task IDs
* no missing dependencies
* no circular dependencies
* at least one root task
* at least one terminal task

Create tests.

Do not execute tasks yet.
