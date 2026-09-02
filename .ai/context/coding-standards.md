# AI Factory — Coding Standards

Extends `AGENTS.md` at the repository root. This file adds C#/.NET
specifics that apply to every project under `apps/src`.

## Language & style

- Nullable reference types enabled everywhere (`<Nullable>enable</Nullable>`
  already set on every `.csproj`).
- `sealed class` for entities and services unless inheritance is a real
  requirement.
- Explicit access modifiers on public/internal members.
- `Guid` identifiers, `DateTime` timestamps stored as UTC (`CreatedAtUtc`,
  `UpdatedAtUtc`, `*AtUtc` suffix convention — keep it consistent).
- Enums for closed sets of states (`ProjectStatus`, `WorkItemType`); store
  via `HasConversion<string>()` in EF configuration so the SQLite column
  stays human-readable.
- Interfaces for every external-infrastructure boundary
  (`IAgentExecutor`, `IModelRouter`, `IContextProvider`, `IWorkflowEngine`,
  repository interfaces). Application code depends on the interface only.

## EF Core / persistence conventions

- Table names: `snake_case` (`projects`, `work_items`, `agent_runs`).
- Column names: default EF `PascalCase` (do not rename columns).
- Configure everything explicitly in `FactoryDbContext.OnModelCreating` —
  no data annotations on entities, no implicit conventions relied upon for
  keys/required fields/max lengths.
- Composite keys via `HasKey(x => new { ... })` (see `WorkItemDependency`).
- Every FK relation must state its `DeleteBehavior` explicitly — pick
  `Cascade` only where deleting the parent should delete the child; use
  `Restrict` to break potential multiple-cascade-path cycles (see
  `WorkItemDependency` → `WorkItem` twice).
- New entity → new migration via `dotnet ef migrations add <Name>` against
  `AI.Factory.Infrastructure.csproj` (project) /
  `AI.Factory.Api.csproj` (startup-project). Never hand-edit a generated
  migration file unless a specific EF-generated bug requires it.

## Error handling & logging

- Never `catch (Exception) { }` silently — log with structured properties
  or rethrow.
- Structured logging (`ILogger<T>`) for every workflow-significant event:
  workflow started/paused/completed, task started/completed/failed, retry
  started, agent selected, model selected, build/test/review started.
- Never log secrets: API keys, tokens, passwords, connection strings with
  credentials.

## Reuse before creation

- Before adding an entity/service/interface, check whether one already
  exists (`Grep`/`Glob` across `apps/src`) — extend it instead of
  duplicating. Phase 1 already diverged from an early draft spec in a few
  places (`Project.RootPath`/`GitRepository`, `WorkItem.FeatureId` hierarchy)
  — treat the current code as the source of truth over any older prompt
  text.

## Validation before "done"

```
dotnet build apps/AI.Factory.slnx
dotnet test apps/AI.Factory.slnx      # when tests exist for the change
dotnet format apps/AI.Factory.slnx    # only if broad formatting drift, not for the touched scope alone
```

Report exact command output, not a claim of success.
