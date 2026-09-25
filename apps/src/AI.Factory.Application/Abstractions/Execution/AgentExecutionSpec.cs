namespace AI.Factory.Application.Abstractions.Execution;

/// <summary>
/// Provider-neutral description of one agent execution, built by the Factory
/// from the AgentRun execution snapshot that AgentOrchestrator already
/// prepares. Plain values only: no domain entities, no repositories, no
/// runtime handles, no secrets. It is not a second domain model — it carries
/// exactly what a runtime needs to execute and what is needed to report the
/// result back onto the existing AgentRun contract.
/// </summary>
/// <param name="Instructions">
/// The full instruction text built by AgentInstructionBuilder (objective,
/// constraints, structured-output contract). How it is combined with
/// <see cref="ExecutionAgent.SystemPrompt"/> is runtime-specific.
/// </param>
public sealed record AgentExecutionSpec(
    Guid AgentRunId,
    Guid CorrelationId,
    Guid? ProjectId,
    Guid? WorkItemId,
    int Attempt,
    int Iteration,
    ExecutionAgent Agent,
    string? Instructions,
    ExecutionWorkspace Workspace,
    ModelRoute Model,
    TimeSpan? Timeout = null,
    DateTimeOffset? Deadline = null);

/// <summary>
/// The agent being executed, as configured on its AgentDefinition. Whether the
/// definition may run at all is Factory policy, decided before any adapter is
/// invoked, so it is deliberately not part of this contract.
/// </summary>
public sealed record ExecutionAgent(
    Guid DefinitionId,
    string Name,
    string AgentType,
    string SystemPrompt);

/// <param name="Path">
/// Factory-resolved workspace (IWorkspaceResolver), or null for a run with no
/// resolved workspace (a Planner-only AgentRun) — the runtime then decides its
/// own isolated scratch location, exactly as before.
/// </param>
public sealed record ExecutionWorkspace(string? Path);

/// <summary>
/// The Factory's model decision for this run. Both parts are independently
/// optional, mirroring AgentRun.Model/AgentRun.Provider: a null ModelId means
/// "the runtime's own configured default applies".
/// </summary>
/// <param name="GatewayKey">The routing provider/gateway id (e.g. an AiProviders key) as persisted on AgentRun.Provider.</param>
public sealed record ModelRoute(string? ModelId, string? GatewayKey);
