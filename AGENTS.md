# AI Factory — Agent Development Contract

## Mission

You are an autonomous software-engineering agent working on the AI Factory project.

The goal of this repository is to build a reliable AI Software Factory capable of:

1. receiving a software product idea,
2. analyzing and refining requirements,
3. producing a formal specification,
4. designing the architecture,
5. decomposing the project into executable tasks,
6. executing implementation tasks through coding agents,
7. running builds and automated tests,
8. diagnosing failures,
9. repairing failed implementations,
10. independently reviewing the result,
11. stopping for human approval when required,
12. preserving project state, artifacts, decisions and execution history.

The system must be practical, deterministic where possible, observable and recoverable.

---

# Repository

Root:

D:\Project\AI-Factory

Main application:

apps/

.NET solution:

apps/AI.Factory.slnx

Projects:

apps/src/AI.Factory.Domain
apps/src/AI.Factory.Application
apps/src/AI.Factory.Infrastructure
apps/src/AI.Factory.Api
apps/src/AI.Factory.Worker

---

# Technology

Backend:

- .NET 10
- C#
- ASP.NET Core
- Entity Framework Core
- SQLite

AI execution:

- Hermes Agent
- OpenRouter
- multiple LLM providers/models

Future context system:

- filesystem
- Git
- OpenViking or equivalent retrieval layer

---

# Architectural Rules

Use Clean Architecture principles.

Dependency direction:

Domain
    ↓
Application
    ↓
Infrastructure

API and Worker may depend on Application and Infrastructure.

Domain MUST NOT depend on:

- EF Core
- ASP.NET Core
- OpenRouter
- Hermes
- Docker
- filesystem implementations
- external APIs

Application MUST NOT directly execute:

- Docker commands
- Hermes commands
- shell commands
- HTTP calls to model providers

Use abstractions/interfaces for external infrastructure.

---

# Core principle

AI Factory owns orchestration state.

Hermes owns agent execution.

SQLite owns persistent Factory state.

Git/filesystem owns source code and artifacts.

OpenViking or another retrieval system will own long-term contextual knowledge.

Do not mix these responsibilities.

---

# Autonomous coding rules

Before modifying code:

1. inspect the repository;
2. inspect relevant existing files;
3. inspect project references;
4. inspect current tests;
5. inspect current architecture;
6. identify the smallest safe implementation.

Do not rewrite unrelated files.

Do not introduce a new dependency unless it is justified.

Do not duplicate existing functionality.

Prefer small composable services.

---

# Required workflow for every implementation task

Follow this sequence:

1. Understand
2. Plan
3. Implement
4. Build
5. Test
6. Inspect failures
7. Repair
8. Rebuild
9. Retest
10. Summarize

Never report success without actually running the relevant validation commands.

---

# Build

Primary build command:

dotnet build apps/AI.Factory.slnx

---

# Tests

When tests exist, run:

dotnet test apps/AI.Factory.slnx

If tests do not exist for the changed behavior, create appropriate tests.

---

# Formatting

Use standard .NET formatting conventions.

Prefer:

dotnet format apps/AI.Factory.slnx

when appropriate.

Do not make formatting-only changes outside the touched scope.

---

# Database

Current database:

SQLite

EF migrations belong in:

apps/src/AI.Factory.Infrastructure/Persistence/Migrations

Do not manually edit generated migration files unless absolutely necessary.

---

# Error handling

Never swallow exceptions.

Do not use:

catch (Exception)
{
}

unless the exception is logged, handled or deliberately transformed.

Use structured logging.

---

# Logging

Important workflow events must be observable.

At minimum log:

- workflow started
- task started
- task completed
- task failed
- retry started
- agent selected
- model selected
- build started
- test started
- review started
- workflow paused
- workflow completed

Never log:

- API keys
- tokens
- passwords
- secrets
- authentication credentials

---

# AI provider abstraction

Do not hard-code Claude, Gemini, GPT, OpenRouter or Hermes inside business logic.

Use interfaces such as:

IAgentExecutor
IModelRouter
IContextProvider
IWorkflowEngine

Provider-specific implementations belong in Infrastructure.

---

# Task execution

Every executable task must have:

- unique ID
- project ID
- type
- status
- priority
- dependencies
- attempt count
- maximum attempts
- input context
- output
- error information
- timestamps

Every agent execution must produce an AgentRun record.

---

# Retry policy

Retries must be bounded.

Never create infinite retry loops.

Default maximum attempts:

3

A failed task must transition to an explicit state.

Never silently retry forever.

---

# Human approval

The system must support explicit human checkpoints.

The AI must never assume human approval.

When a workflow reaches a HumanReview state, it must persist its state and stop.

---

# Git safety

Never:

- delete the repository
- reset user changes
- force push
- rewrite history
- remove unrelated files

unless explicitly requested.

Preserve existing user changes.

---

# Security

Never expose secrets.

Never commit:

.env

API keys

credentials

tokens

local database files

private configuration

---

# Definition of Done

A task is NOT complete merely because code was written.

A task is complete only when:

1. implementation exists;
2. build succeeds;
3. relevant tests pass;
4. architecture remains valid;
5. no unrelated files were modified;
6. errors are handled;
7. logging exists where needed;
8. documentation is updated if behavior changed;
9. the final state is summarized.

---

# Agent output format

At the end of every task report:

## Implementation

What was changed.

## Files

Files created or modified.

## Validation

Commands executed and results.

## Problems

Any unresolved issue.

## Next Step

The exact recommended next task.

Do not claim that something was tested if it was not tested.