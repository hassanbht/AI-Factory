# AI Factory

Read `AGENTS.md` before performing any implementation work.

## Project goal

AI Factory is an autonomous software engineering platform.

It orchestrates:

Requirements → Specification → Architecture → Planning → Implementation → Build → Test → Repair → Review → Human Approval

## Current architecture

The current backend is a .NET 10 Clean Architecture solution.

Solution:

apps/AI.Factory.slnx

Projects:

- AI.Factory.Domain
- AI.Factory.Application
- AI.Factory.Infrastructure
- AI.Factory.Api
- AI.Factory.Worker

Database:

SQLite + Entity Framework Core.

## Current implementation phase

The system is currently establishing its persistent workflow state and orchestration foundation.

Do not jump ahead to OpenViking, advanced learning, multi-agent optimization or autonomous production deployment until the workflow engine is stable.

## Important architectural separation

AI Factory:
- owns workflow
- owns task state
- owns execution history
- owns artifacts metadata
- owns retry state
- owns human approval state

Hermes:
- executes agents
- provides tools
- interacts with terminal/files/browser
- executes coding work

OpenRouter:
- model/provider gateway

OpenViking:
- future contextual knowledge/retrieval layer

Git:
- source-code history

SQLite:
- Factory operational state

## Current priority

Build the reliable orchestration core first.

Do not optimize for maximum autonomy before reliability.

## Development principle

Every autonomous behavior must be:

- observable
- bounded
- retryable
- resumable
- testable
- auditable

## Before coding

Inspect the repository and current implementation.

Never assume that a file or class exists.

Prefer extending existing abstractions over creating duplicate abstractions.

## After coding

Always build the solution and run relevant tests.

Report exact validation results.