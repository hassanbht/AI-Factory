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
/// The Factory's model decision for this run.
/// Captures provider-neutral model and gateway routing decisions.
/// NEVER contains plaintext credentials.
/// </summary>
/// <param name="ModelId">Model identifier (e.g. deepseek/deepseek-v4-flash-0731).</param>
/// <param name="GatewayKey">The routing gateway id (configured gateway identifier).</param>
/// <param name="Provider">Optional model provider override if distinct from gateway.</param>
/// <param name="CredentialReference">Optional secure reference to credential secret name.</param>
public sealed record ModelRoute(
    string? ModelId,
    string? GatewayKey = null,
    string? Provider = null,
    string? CredentialReference = null);

