using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// Execution-adapter migration Phase B — TRANSITIONAL. Exposes the existing
/// Hermes execution path as the "Hermes" <see cref="IExecutionAdapter"/> by pure
/// delegation to the unchanged <see cref="HermesAgentRuntime"/>, which stays the
/// single source of truth for Hermes behaviour (prompt construction, docker/CLI
/// arguments, resilience decoration, timeout/failure classification, artifact
/// recording). This class only translates shapes:
/// spec -> the AgentRun snapshot HermesAgentRuntime already consumes, and its
/// AgentRunExecutionResult -> <see cref="ExecutionOutcome"/>.
///
/// Phase C replaces it with a real HermesExecutionAdapter that owns that logic
/// directly; until then nothing Hermes-specific may be added here.
/// </summary>
public sealed class LegacyHermesRuntimeAdapter : IExecutionAdapter
{
    public const string AdapterKey = "Hermes";

    private readonly HermesAgentRuntime _runtime;

    public LegacyHermesRuntimeAdapter(HermesAgentRuntime runtime)
    {
        _runtime = runtime;
    }

    public string Key => AdapterKey;

    // Hermes has always received AgentRun.Model as its -m value.
    public ExecutionCapabilities Capabilities => ExecutionCapabilities.FactoryRoutedModel;

    public async Task<ExecutionOutcome> ExecuteAsync(
        AgentExecutionSpec spec,
        CancellationToken cancellationToken = default)
    {
        var result = await _runtime.ExecuteAsync(ToAgentRun(spec), cancellationToken);

        return new ExecutionOutcome(
            Status: result.Success
                ? ExecutionOutcomeStatus.Succeeded
                : result.FailureCategory switch
                {
                    FailureCategory.Timeout => ExecutionOutcomeStatus.TimedOut,
                    FailureCategory.Cancelled => ExecutionOutcomeStatus.Cancelled,
                    _ => ExecutionOutcomeStatus.Failed
                },
            Output: result.Output,
            ErrorMessage: result.ErrorMessage,
            FailureCategory: result.FailureCategory,
            DurationMs: result.DurationMs,
            Usage: result.PromptTokens is null && result.CompletionTokens is null && result.TotalTokens is null
                ? null
                : new ExecutionUsage(result.PromptTokens, result.CompletionTokens, result.TotalTokens),
            ArtifactReferences: result.ArtifactReferences,
            ToolCallNames: result.ToolCallNames,
            Model: new ModelRoute(result.Model, result.Provider));
    }

    /// <summary>
    /// Rebuilds the non-persisted execution snapshot with every field
    /// HermesAgentRuntime reads (definition name/prompt/enabled, ids, correlation,
    /// provider, model, input, workspace). Never saved.
    /// </summary>
    private static AgentRun ToAgentRun(AgentExecutionSpec spec) => new()
    {
        Id = spec.AgentRunId,
        AgentDefinitionId = spec.Agent.DefinitionId,
        AgentDefinition = new AgentDefinition
        {
            Id = spec.Agent.DefinitionId,
            Name = spec.Agent.Name,
            AgentType = spec.Agent.AgentType,
            SystemPrompt = spec.Agent.SystemPrompt,
            Enabled = spec.Agent.Enabled
        },
        ProjectId = spec.ProjectId,
        WorkItemId = spec.WorkItemId,
        Attempt = spec.Attempt,
        Iteration = spec.Iteration,
        CorrelationId = spec.CorrelationId,
        Provider = spec.Model.GatewayKey,
        Model = spec.Model.ModelId,
        Input = spec.Instructions,
        WorkspacePath = spec.Workspace.Path
    };
}
