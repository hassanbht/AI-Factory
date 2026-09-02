# AI Factory — Architecture Context

## Solution layout

```
apps/AI.Factory.slnx
apps/src/AI.Factory.Domain
apps/src/AI.Factory.Application
apps/src/AI.Factory.Infrastructure
apps/src/AI.Factory.Api
apps/src/AI.Factory.Worker
```

Dependency direction (Clean Architecture):

```
Domain
  ↓
Application
  ↓
Infrastructure
```

API and Worker may depend on Application and Infrastructure.

Domain MUST NOT depend on: EF Core, ASP.NET Core, OpenRouter, Hermes, Docker,
filesystem implementations, external APIs.

Application MUST NOT directly execute: Docker commands, Hermes commands,
shell commands, HTTP calls to model providers. Use interfaces; provider
implementations live in Infrastructure.

## Responsibility separation

| Owner | Responsibility |
|---|---|
| AI Factory (SQLite) | workflow state, task state, execution history, artifact metadata, retry state, human-approval state |
| Hermes | agent execution, tools, terminal/files/browser, coding work |
| OpenRouter | model/provider gateway |
| OpenViking (future) | long-lived contextual knowledge/retrieval — never authoritative workflow state |
| Git / filesystem | source-code history and produced artifacts |

Do not mix these. Never store authoritative workflow state anywhere but
SQLite; never let OpenViking or Hermes state become a second source of truth.

## Domain model (current — Phase 1)

Entities: `Project`, `WorkItem`, `WorkItemDependency`, `AgentRun`, `Artifact`,
`Feature`, `Requirement` (pre-existing hierarchy, kept as-is).

Enums: `ProjectStatus`, `WorkItemStatus` (string-valued on `WorkItem.Status`),
`WorkItemType`.

Persistence: `AI.Factory.Infrastructure.Persistence.FactoryDbContext` (EF
Core 10 + SQLite). Migrations under
`apps/src/AI.Factory.Infrastructure/Persistence/Migrations`.

## Core abstractions (owned by Application, implemented by Infrastructure)

| Interface | Status | Implementation |
|---|---|---|
| `IProjectRepository` | implemented | `ProjectRepository` (EF Core) |
| `IAgentExecutor` | interface only | `HermesAgentExecutor` — planned, phase "Hermes Executor" |
| `IWorkflowEngine` / `IWorkflowStateStore` / `IWorkflowTransitionValidator` | planned | phase "Workflow Engine" |
| `ITaskScheduler` / `ITaskDependencyResolver` / `ITaskStateManager` | planned | phase "Task DAG" |
| `IModelRouter` | planned | phase "Model Router" |
| `IContextProvider` | planned | filesystem/DB first, `OpenVikingContextProvider` later |
| `ITaskPlanner` | planned | phase "Task Planner" |
| `IBuildExecutor` / `ITestExecutor` / `IValidationEngine` | planned | phase "Build/Test" |
| `IEvaluationEngine` / `ILearningStore` | planned | phase "Evaluation"/"Learning" — last |

Never hard-code Claude/Gemini/GPT/OpenRouter/Hermes inside business logic —
always go through the interfaces above.

## Build order (do not skip ahead)

`01 Domain → 02 Persistence → 03 Workflow Engine → 04 Task DAG →
05 Hermes Executor → 06 Model Router → 07 Context Engine → 08 Task Planner →
09 Autonomous Coding → 10 Build/Test → 11 Repair → 12 Independent Review →
13 Human Approval → 14 OpenViking → 15 Evaluation → 16 Learning →
17 Production Hardening`

Each phase has its own prompt file under `.ai/prompts/`. Read the matching
file and implement exactly that phase — do not implement future phases
speculatively.
