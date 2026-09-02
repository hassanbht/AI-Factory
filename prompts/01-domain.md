# Implement AI Factory Domain Model

Read:

- AGENTS.md
- CLAUDE.md
- .ai/context/architecture.md
- .ai/state/implementation-status.md

Inspect the existing Domain project before making changes.

Implement only the domain model required for the persistent orchestration foundation.

Required concepts:

Project

WorkItem

WorkItemDependency

AgentRun

Artifact

ProjectStatus

WorkItemStatus

WorkItemType

Requirements:

- Domain must remain independent from Infrastructure.
- Do not add EF Core references to Domain.
- Do not add ASP.NET references to Domain.
- Use Guid identifiers.
- Use UTC timestamps.
- Use explicit enums.
- Keep entities persistence-friendly.
- Avoid unnecessary domain complexity.

Before implementation:

Explain the model and relationships.

Then implement it.

After implementation:

Run:

dotnet build apps/AI.Factory.slnx

Inspect the generated code for architectural violations.

Do not implement the workflow engine yet.

Do not implement Hermes integration yet.

Do not implement OpenRouter yet.

Do not implement OpenViking yet.

Do not modify unrelated projects.

Update:

.ai/state/implementation-status.md

only for work actually completed.

Final report:

## Changes

## Files

## Build Result

## Remaining Work