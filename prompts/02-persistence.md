# Implement AI Factory Persistence Layer

Read:

- AGENTS.md
- CLAUDE.md
- .ai/context/architecture.md
- .ai/state/implementation-status.md

Inspect the current Domain entities before implementation.

Implement the persistence layer using:

- Entity Framework Core 10
- SQLite

Create:

FactoryDbContext

Repository abstractions where appropriate.

Configure:

- Project
- WorkItem
- WorkItemDependency
- AgentRun
- Artifact

Requirements:

- composite key for WorkItemDependency
- proper foreign keys
- cascade behavior must be intentional
- indexes for project/status lookup
- unique project slug
- no EF Core dependency in Domain
- no business logic in DbContext

Create an EF migration.

Create the SQLite database.

Validate:

dotnet build apps/AI.Factory.slnx

dotnet ef migrations list \
  --project apps/src/AI.Factory.Infrastructure/AI.Factory.Infrastructure.csproj \
  --startup-project apps/src/AI.Factory.Api/AI.Factory.Api.csproj

dotnet ef database update \
  --project apps/src/AI.Factory.Infrastructure/AI.Factory.Infrastructure.csproj \
  --startup-project apps/src/AI.Factory.Api/AI.Factory.Api.csproj

Do not implement the workflow engine.

Do not integrate Hermes.

Do not integrate OpenRouter.

Do not integrate OpenViking.

If an existing implementation already satisfies a requirement, do not recreate it.

Update implementation-status.md only after successful validation.