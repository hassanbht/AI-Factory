# Phase 18.1.2 — Agent Domain Model & AgentRun Lifecycle

## 1. Objective

Establish the canonical Agent domain model and durable AgentRun lifecycle on top of the Phase 18.1.1 LLM provider/routing/resilience foundation, without yet building the API, Worker orchestration, tool system, memory system, or production Hermes runtime.

## 2. Why the old and new AgentRun had conflicting responsibilities

Before this phase, `AgentRun` was a flat record tied 1:1 to a `WorkItem`, storing the outcome of one Hermes CLI process invocation: `bool Success`, `AttemptNumber`, `Prompt`/`Response`/`Error`. It answered "how did this one Hermes process execution go?" — infrastructure-level, runtime-specific detail.

Phase 18.1.2 needs a different concept: a stateful, runtime-agnostic execution record for *any* agent-driven unit of work (analysis, planning, coding, review, repair, validation), independent of WorkItem being its identity and independent of Hermes being the executor. Reusing the name `AgentRun` for both would have been the same architectural drift the AI Factory Agent Runtime spec explicitly asks not to repeat.

**Resolution**: the legacy entity was renamed to `HermesExecution` (table `hermes_executions`) — it keeps its exact original shape and responsibility (one physical Hermes process attempt per WorkItem). The name `AgentRun` was freed for the new canonical model. All existing consumers (`AutonomousCodingOrchestrator`, `LocalContextProvider`, DI registrations, tests) were updated to reference `HermesExecution`/`IHermesExecutionRepository` instead.

## 3. What AgentDefinition represents

`AgentDefinition` (`apps/src/AI.Factory.Domain/Entities/AgentDefinition.cs`) is WHAT an agent is: `Id`, `Name`, `Description`, `AgentType` (free-text role), `SystemPrompt`, `Enabled`, `CreatedAtUtc`/`UpdatedAtUtc`, `Version` (optimistic concurrency via `IVersionedEntity`).

## 4. What AgentRun represents

`AgentRun` (`apps/src/AI.Factory.Domain/Entities/AgentRun.cs`) is WHICH execution of an `AgentDefinition` this is: `AgentDefinitionId` (required FK), optional `ProjectId`/`WorkItemId` (plain scalars, no navigation/FK — see §5), `Status` (`AgentRunStatus` enum), `Iteration`, `Attempt`, `CorrelationId` (idempotency key, unique), `Provider`/`Model`, timestamps, `Input`/`Output` (summary text, not raw payload storage), token counts, `ErrorMessage`/`FailureCategory` (reusing the existing cross-cutting `Domain.Enums.FailureCategory`), `Version`.

### Status model

```
Created  -> Queued
Queued   -> Running
Running  -> Waiting | Succeeded | Failed | TimedOut | Cancelled
Waiting  -> Running | Failed | Cancelled
```

Enforced centrally by `AgentRunTransitions` (static allow-list + terminal-status check) and applied only through `AgentRun.TransitionTo(status)`, which throws `InvalidAgentRunTransitionException` on an invalid transition and never mutates `Status` directly. `TransitionTo` also stamps `StartedAtUtc` on first entry to `Running` and `CompletedAtUtc` on any terminal transition.

## 5. How WorkItem relates to AgentRun

`AgentRun.WorkItemId` is optional and deliberately a plain `Guid?` scalar with **no** EF navigation property and **no** foreign key — matching the existing `OrchestrationRun` precedent. A WorkItem *may* cause an AgentRun, but AgentRun does not depend on WorkItem for its identity or lifecycle, and an AgentRun's history must survive a WorkItem being deleted. (An earlier attempt at this added a `WorkItem` navigation property; EF's convention-based relationship discovery silently created an FK from it despite `OnModelCreating` never asking for one, which broke inserts referencing a nonexistent WorkItem. The fix was removing the navigation property entirely, not adding a Fluent override — see `AgentRun.cs` for the note.)

## 6. How Hermes execution relates to AgentRun

They are peers at different layers, not the same concept:

```
AgentDefinition
       |
     AgentRun            <-- orchestration-level, runtime-agnostic
       |
   IAgentRuntime          <-- contract only (this phase)
       |
 HermesRuntime            <-- future Infrastructure adapter (not built yet)
       |
 HermesExecution           <-- physical Hermes process audit row (pre-existing, renamed)
```

`IAgentExecutor`/`HermesAgentExecutor` (Phase 17.x) continue unchanged — they are the existing Hermes-process execution path used by `AutonomousCodingOrchestrator` today. `IAgentRuntime` is a new, separate contract for a future AgentRun-driven execution loop; nothing in this phase wires the two together, and nothing requires that they ever be the same call path.

## 7. Contracts introduced (interfaces only, no implementation)

- `IAgentRuntime` (`Application/Abstractions/IAgentRuntime.cs`) — `ExecuteAsync(AgentRun) -> AgentRunExecutionResult`. Result contract carries success/output/token usage/duration/artifact references/tool-call names/error info/provider+model metadata, per §10 of the spec — large payloads are referenced, not inlined.
- `IAgentTool` (`Application/Abstractions/IAgentTool.cs`) — `Name`, `Description`, `ExecuteAsync(AgentToolContext) -> AgentToolResult`. No implementations, no execution wiring.
- `IAgentMemory` (`Application/Abstractions/IAgentMemory.cs`) — `StoreAsync`/`SearchAsync` over `AgentMemoryEntry`/`AgentMemoryQuery`. No implementation, no OpenViking dependency.

None of these three reference Hermes, OpenRouter, OpenViking, HTTP, CLI execution, or Docker — confirmed by grep across `Application/Abstractions` and `Domain`.

## 8. Why OpenViking is intentionally not part of this phase

Per the Phase 18.0 architecture, OpenViking is a future contextual-memory adapter. `IAgentMemory` establishes the boundary it will eventually implement; introducing an actual OpenViking dependency now would couple the Agent domain to an unbuilt external system before the contract it needs to satisfy even has a first caller.

## 9. Persistence

New tables: `agent_definitions`, `agent_runs` (canonical). Renamed table: `agent_runs` → `hermes_executions` (legacy).

Indexes on `AgentRun`: `AgentDefinitionId`, `ProjectId`, `WorkItemId`, `Status`, `CreatedAtUtc`, and a **unique** index on `CorrelationId` (the idempotency guarantee — see §10). Index on `AgentDefinition.Enabled`.

Migration: `20260909075451_AddAgentDomainModel`. **Hand-edited** after generation: EF Core's own `dotnet ef migrations add` diff engine matched the old `AgentRun` CLR type name against the new canonical `AgentRun` type of the same name and produced a data-corrupting `RenameColumn` plan (`Success` → `Version`, `Response` → `ProjectId`, etc.) instead of recognizing this as two unrelated tables. The migration was rewritten by hand to use `RenameTable`/`RenameIndex` for the legacy table (data-preserving) and plain `CreateTable` for the two genuinely new tables. Verified empirically (see §12).

## 10. Idempotency and concurrency

`IAgentRunRepository.CreateAsync` inserts optimistically; on `DbUpdateException` (the unique `CorrelationId` index rejecting a duplicate) it re-queries by `CorrelationId` and returns the existing row instead of throwing — so two callers racing to create an AgentRun for the same logical request converge on one canonical row. The database's unique index is the final guarantee; the repository just makes the race resolve gracefully instead of surfacing a raw constraint-violation exception. Verified by `AgentRunRepositoryTests.CreateAsync_CalledTwiceWithSameCorrelationId_ReturnsSameAgentRun`.

`AgentRun`/`AgentDefinition` both implement `IVersionedEntity` (optimistic concurrency `Version` token), consistent with `OrchestrationRun`/`Project`/etc.

## 11. Tests added

- `AgentRunTransitionsTests` — every documented valid/invalid transition, terminal-status checks, `TransitionTo` side effects (`StartedAtUtc`/`CompletedAtUtc`), invalid-transition exception shape.
- `AgentRunRepositoryTests` — `AgentDefinition` create/reload/enable-disable; `AgentRun` create/reload with Project/WorkItem references; idempotent create under a duplicate `CorrelationId`; status-transition persistence across reload.
- `AgentRunMigrationTests` — migration applies to a clean database; migration applied from the pre-Phase-18.1.2 baseline preserves a legacy `agent_runs` row intact under `hermes_executions` (all fields verified) and the old name now holds none of that data.

All existing tests touched by the rename (`AutonomousCodingOrchestratorTests`, `LocalContextProviderTests`, `ReviewContextBuilderTests`, `OrchestrationInstrumentationTests`) were updated to the new `HermesExecution`/`IHermesExecutionRepository` names and continue to pass unmodified in behavior.

## 12. Migration validation actually performed

- `dotnet ef database update` against a fresh SQLite file — full chain applies cleanly (Implemented + Validated locally).
- `AgentRunMigrationTests.Migrate_ToLatest_OnCleanDatabase_Succeeds` — same, as an automated regression (Validated locally).
- `AgentRunMigrationTests.Migrate_ToLatest_FromPreAgentDomainModelBaseline_PreservesLegacyAgentRunAsHermesExecution` — migrates to the pre-existing `20260903091500_AddOptimisticConcurrencyTokens` baseline, inserts a legacy-shaped row via raw ADO.NET, migrates to latest, and asserts every field of that row is intact under `HermesExecutions` and absent from the new `AgentRuns` table (Validated locally).
- Full solution test suite (`AI.Factory.Application.Tests` + `AI.Factory.Api.Tests` + `AI.Factory.Worker.Tests`, 731 tests) run 3 consecutive times, 0 failures each time (Validated locally).
- Not validated: Docker/container migration execution, GitHub Actions CI, any real external LLM provider call (out of scope for this phase; unchanged from Phase 18.1.1).

## 13. Implemented / Validated / Not implemented / Future work

**Implemented**: `AgentDefinition`, `AgentRun` (canonical, stateful), `AgentRunStatus` + `AgentRunTransitions`, `HermesExecution` (renamed legacy entity), `IAgentDefinitionRepository`/`IAgentRunRepository`/`IHermesExecutionRepository` + EF implementations, `IAgentRuntime`/`IAgentTool`/`IAgentMemory` contracts (interfaces only), EF configuration (indexes, unique `CorrelationId`, concurrency tokens), migration.

**Validated**: build (0 warnings/errors), full test suite (731 tests, 3 consecutive green runs), migration on clean DB, migration preserving legacy data from the pre-existing baseline.

**Not implemented** (explicitly out of scope per spec §11): `/api/agents/*`, Agent orchestration service, Worker integration, Hermes `IAgentRuntime` implementation, production `IAgentTool`/`IAgentMemory` implementations, OpenViking integration, dashboard/UI, multi-agent delegation, autonomous retry loops.

**Future work** (per spec §"Definition of Done"): Phase 18.1.3 → Hermes `IAgentRuntime`; Phase 18.1.4 → Tools + Memory; Phase 18.1.5 → Agent Orchestration + Worker; Phase 18.1.6 → Agent API + Metrics; Phase 18.1.7 → End-to-End / Stability Gate.

## 14. Known limitations

- `AgentRun.Output`/`Input` are plain nullable text columns, not a dedicated artifact-reference scheme — acceptable for this phase (no runtime produces large payloads yet) but will need revisiting once `IAgentRuntime` has a real implementation producing substantial output.
- No repository method for listing/paginating AgentRuns yet — deferred until the API phase actually needs it (YAGNI).
- `AgentDefinition` has no seed data or admin surface — deferred to the API phase.
