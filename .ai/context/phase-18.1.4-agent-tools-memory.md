# Phase 18.1.4 — Agent Tools & Memory Layer

## Status: GO

## Architecture

```text
AgentRun
   │
   ├── IAgentRuntime          (Phase 18.1.3, unchanged)
   │
   ├── IAgentToolRegistry
   │       └── IAgentTool[]   (ReadArtifact today)
   │
   └── IAgentMemory
           └── LocalAgentMemoryProvider (SQLite, via FactoryDbContext)
```

```text
Factory DB = workflow truth
Memory     = contextual knowledge
Hermes     = execution runtime
```

## IAgentTool

`AI.Factory.Application.Abstractions.IAgentTool` — evolved from the Phase
18.1.2 placeholder. Runtime-agnostic: the same tool is usable by Hermes,
Claude, Codex, or another runtime without this contract changing.

```csharp
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    string InputSchema { get; }   // JSON Schema string
    bool Enabled { get; }
    Task<AgentToolResult> ExecuteAsync(AgentToolContext context, CancellationToken cancellationToken = default);
}

public sealed record AgentToolContext(Guid AgentRunId, Guid? AgentDefinitionId, Guid? CorrelationId, string InputJson);
public sealed record AgentToolResult(bool Success, string? OutputJson, string? ErrorMessage, long DurationMs);
```

## Tool Registry

`IAgentToolRegistry` (`AI.Factory.Application.Abstractions`), implemented by
`AgentToolRegistry` (`AI.Factory.Application.Tools`). Built once from the
DI-registered `IEnumerable<IAgentTool>`:

- Duplicate tool names throw `InvalidOperationException` at construction —
  deterministic, not a silent pick.
- `GetDescriptors()` returns only `Enabled` tools, sorted by name.
- `ExecuteAsync(name, context, ct)` resolves the tool, measures duration,
  logs `ToolExecutionStarted`/`ToolExecutionCompleted`/`ToolExecutionFailed`,
  and translates any thrown exception (other than
  `OperationCanceledException`, which propagates) into a failed
  `AgentToolResult` rather than throwing. Unknown or disabled tool names
  fail the same way instead of being resolved.

This is the single execution entry point for tools — no duplicate execution
logic exists inside `HermesAgentRuntime`, which is untouched this phase per
the phase spec (tool/memory injection into the runtime is deferred).

## Implemented Tool: ReadArtifact

`AI.Factory.Infrastructure.Agents.ReadArtifactTool` — the one built-in tool
this phase, read-only.

- Input: `{"artifactId": "<guid>"}` — an agent can only name an ArtifactId,
  never a raw filesystem path.
- Resolves the artifact via `IArtifactRepository.GetByIdAsync` (new method
  added to the existing repository), then resolves and validates the
  recorded `Artifact.Path` through the existing
  `WorkspacePathGuard.EnsureWithinRoot` (reused from Phase 18.1.3's
  `HermesAgentExecutor`) against `HermesOptions.WorkspaceRoot`.
- Rejects a path that resolves outside the workspace root
  (`SecurityViolationException` → failed result).
- Rejects filenames matching a secret-shaped denylist (`.env`, `id_rsa`,
  `id_ed25519`, `.pem`, `.key`, `credentials`, `.npmrc`, `.netrc`, `secret`)
  before ever reading the file.
- Caps returned content at 200,000 characters (`ponytail:` marked ceiling —
  raise or paginate if real artifacts exceed it).
- Never returns the absolute host path in its output — only artifact
  `Name`/`ArtifactType`/`Content`.

No unrestricted shell or filesystem tool was implemented — out of scope per
the phase spec.

## Security Boundaries

- `ReadArtifact` never accepts a raw path from agent input.
- `WorkspacePathGuard` resolves symlinks/`..` fully before comparing against
  the configured root (existing Phase 18.1.3 utility, reused, not
  duplicated).
- Secret-shaped filenames are rejected before file access.

## IAgentMemory

`AI.Factory.Application.Abstractions.IAgentMemory` — evolved from the Phase
18.1.2 placeholder to add explicit scope fields (the placeholder's
`AgentRunId`/`AgentDefinitionId` were both required and did not express
Project or Global scope):

```csharp
public interface IAgentMemory
{
    Task StoreAsync(AgentMemoryEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentMemoryEntry>> SearchAsync(AgentMemoryQuery query, CancellationToken cancellationToken = default);
}

public sealed record AgentMemoryEntry(
    Guid Id, Guid? ProjectId, Guid? AgentDefinitionId, Guid? AgentRunId,
    string Type, string Content, IReadOnlyDictionary<string, string>? Metadata, DateTime CreatedAtUtc);

public sealed record AgentMemoryQuery(
    Guid? ProjectId = null, Guid? AgentDefinitionId = null, Guid? AgentRunId = null,
    string? Type = null, string? SearchText = null, int MaxResults = 20);
```

Memory is contextual knowledge to help an agent execute — it is never
Factory workflow state. `AgentRun`/`WorkItem` remain the source of truth for
orchestration; memory is not consulted or written by the orchestrator in
this phase.

## Memory Scopes

Scope is expressed by which of `ProjectId`/`AgentDefinitionId`/`AgentRunId`
a query supplies, not by a separate taxonomy field (kept minimal per the
phase spec — "do not overdesign the taxonomy"):

- `ProjectId` set, others omitted → all memory for that project.
- `ProjectId` + `AgentRunId` → memory scoped to one run within a project.
- `ProjectId == null` → an **explicit** request for global memory. Omitting
  it is never a leaky default — `LocalAgentMemoryProvider.SearchAsync`
  filters on `x.ProjectId == query.ProjectId` unconditionally, so a
  global query (`null`) only ever matches rows stored with `ProjectId ==
  null`, and a project-scoped query only ever matches that project's rows.
  The provider enforces this itself; callers cannot bypass it by mistake.

## Local Memory Implementation

`AI.Factory.Infrastructure.Persistence.LocalAgentMemoryProvider` — backed by
the existing Factory SQLite database via `FactoryDbContext.AgentMemories`
(no second database). New Domain entity `AgentMemoryRecord`, table
`agent_memory`, migration `20260909101215_AddAgentMemory`. Metadata
dictionary is JSON-serialized into a `MetadataJson` column (kept a plain
string column so the entity itself has no serializer dependency).

Search is **lexical only** (`EF.Functions.Like` substring match on
`Content`) — documented here rather than pretending at semantic/vector
relevance, per the phase spec. Ordering is deterministic:
`CreatedAtUtc DESC, Id ASC`, then `Take(MaxResults)`.

## OpenViking Boundary

Not implemented this phase — no `OpenVikingMemoryProvider` was added.
`IAgentMemory` is provider-neutral and ready for a future adapter (mirroring
how `IContextProvider` already switches between `LocalContextProvider` and
`OpenVikingContextProvider` based on `OpenVikingOptions.Enabled`), but no
OpenViking-specific code exists anywhere in this phase's changes.

## Runtime Relationship

`HermesAgentRuntime` was **not modified**. It still does not populate
`AgentRunExecutionResult.ToolCallNames`, and does not mutate `AgentRun`
state — both remain deferred, per the Phase 18.1.3 doc, to the orchestration
work in Phase 18.1.5. Wiring tools/memory into an actual agent execution
loop is explicitly out of scope here.

## DI Registration

`Api/Program.cs` and `Worker/Program.cs` (identical block in both):

```csharp
builder.Services.AddScoped<IAgentTool, ReadArtifactTool>();
builder.Services.AddScoped<IAgentToolRegistry, AgentToolRegistry>();
builder.Services.AddScoped<IAgentMemory, LocalAgentMemoryProvider>();
```

Adding another built-in tool later is one more `AddScoped<IAgentTool, ...>()`
line — the registry collects the whole `IEnumerable<IAgentTool>`.

## Testing Strategy

Hand-rolled fakes only (this repo's existing convention — no Moq):

- `FakeAgentTool` (`AI.Factory.Application.Tests`) — `Mode` enum
  (Success/Failure/Throw/Cancel), mirrors `FakeAgentRuntime`.
- `AgentToolRegistryTests` — duplicate-name rejection, enumerate/exclude
  disabled, execute success/failure/thrown-exception/cancellation, unknown
  and disabled tool names.
- `ReadArtifactToolTests` — success read inside a real temp workspace root,
  artifact not found, missing/invalid input JSON, path outside workspace
  root rejected, secret-shaped filename rejected (and the secret content
  never appears in the error message).
- `LocalAgentMemoryProviderTests` — SQLite in-memory (`Data Source=:memory:`,
  `EnsureCreated`), matching `AgentRunRepositoryTests`'s pattern: store then
  retrieve, missing-scope search returns empty, `MaxResults` respected,
  deterministic newest-first ordering, `SearchText` filtering, Project A/B
  isolation, AgentRun isolation within one project, global memory returned
  only on an explicit `ProjectId: null` query, cancellation propagates.

## What Remains for Orchestration (Phase 18.1.5+)

- No orchestrator wires `IAgentToolRegistry`/`IAgentMemory` into an actual
  `AgentRun` execution loop yet — `HermesAgentRuntime` still runs without
  tool calls or memory reads/writes.
- `AgentRunExecutionResult.ToolCallNames` remains unpopulated.
- No `IOperationalMetrics` hooks were added for tool/memory operations —
  deferred to Phase 18.1.6 ("Agent API + Metrics") per the existing
  Phase 18.1.3 note; this phase's `ILogger` events (`ToolExecutionStarted`/
  `Completed`/`Failed`) are the only observability added.
- Only one built-in tool exists (`ReadArtifact`); no write/shell/browser
  tool — explicitly out of scope.

## Migration Status

`20260909101215_AddAgentMemory` — adds table `agent_memory` only. No prior
migration was modified. Verified: clean-DB `dotnet ef database update`
against a scratch SQLite file applies all migrations including this one
without error (see Validation below).

## Recommended Phase 18.1.5

Agent Orchestration Service + Worker — wire `IAgentToolRegistry` and
`IAgentMemory` into an actual `AgentRun` execution loop (populate
`ToolCallNames`, decide when/how memory is read before and written after a
run), without rewriting `HermesAgentRuntime`'s existing contract.
