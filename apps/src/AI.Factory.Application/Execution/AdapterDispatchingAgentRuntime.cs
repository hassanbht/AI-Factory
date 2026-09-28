using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Execution;

/// <summary>
/// Execution-adapter migration Phase B/C — the <see cref="IAgentRuntime"/> the
/// Factory registers. It keeps IAgentRuntime's role (AgentOrchestrator and every
/// test double are unchanged) and adds the Factory-side dispatch steps:
/// AgentDefinition policy (a disabled definition never reaches an adapter), then
/// AgentType -> profile -> <see cref="IExecutionAdapter"/>, then maps the
/// orchestrator's execution snapshot to a provider-neutral
/// <see cref="AgentExecutionSpec"/> and the adapter's
/// <see cref="ExecutionOutcome"/> back onto <see cref="AgentRunExecutionResult"/>,
/// tagged with the adapter key and its exit reason.
///
/// It is not a runtime: no processes, no containers, no persistence, no lifecycle
/// transitions, no acceptance or review. Adapter exceptions (including
/// cancellation) propagate unchanged. Policy and selection failures fail closed
/// as a classified Validation result, so the orchestrator persists a terminal
/// Failed AgentRun instead of leaving it Running.
/// </summary>
public sealed class AdapterDispatchingAgentRuntime : IAgentRuntime, IAgentExecutionDispatcher
{
    private readonly ExecutionProfileResolver _resolver;
    private readonly IFailureClassifier _failureClassifier;
    private readonly ILogger<AdapterDispatchingAgentRuntime> _logger;

    public AdapterDispatchingAgentRuntime(
        ExecutionProfileResolver resolver,
        IFailureClassifier failureClassifier,
        ILogger<AdapterDispatchingAgentRuntime> logger)
    {
        _resolver = resolver;
        _failureClassifier = failureClassifier;
        _logger = logger;
    }

    public async Task<ExecutionOutcome> DispatchAsync(
        AgentExecutionSpec spec,
        CancellationToken cancellationToken = default)
    {
        var agentType = spec.Agent.AgentType;
        var resolution = _resolver.Resolve(agentType);

        if (!resolution.IsResolved)
        {
            var failureReason = resolution.FailureReason ?? "Execution adapter could not be resolved.";
            _logger.LogWarning(
                "ExecutionAdapterResolutionFailed AgentRunId={AgentRunId} AgentType={AgentType} Profile={Profile} "
                    + "Adapter={Adapter} CorrelationId={CorrelationId} Reason={Reason}",
                spec.AgentRunId, agentType, resolution.ProfileName, resolution.AdapterKey,
                spec.CorrelationId, failureReason);

            return new ExecutionOutcome(
                ExecutionOutcomeStatus.Failed,
                Output: null,
                ErrorMessage: failureReason,
                FailureCategory: FailureCategory.Validation,
                DurationMs: 0,
                Usage: null,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                Model: spec.Model,
                ExitReason: "resolution_failed");
        }

        var adapter = resolution.Adapter!;

        if (spec.Model.ModelId is not null && !adapter.Capabilities.HasFlag(ExecutionCapabilities.FactoryRoutedModel))
        {
            var reason =
                $"Execution adapter '{adapter.Key}' (profile '{resolution.ProfileName}') cannot honour the routed model "
                + $"'{spec.Model.ModelId}'.";

            _logger.LogWarning(
                "ExecutionAdapterContractUnsatisfied AgentRunId={AgentRunId} Adapter={Adapter} CorrelationId={CorrelationId} Reason={Reason}",
                spec.AgentRunId, adapter.Key, spec.CorrelationId, reason);

            return new ExecutionOutcome(
                ExecutionOutcomeStatus.Failed,
                Output: null,
                ErrorMessage: reason,
                FailureCategory: FailureCategory.Validation,
                DurationMs: 0,
                Usage: null,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                Model: spec.Model,
                ExitReason: "capability_unsatisfied");
        }

        _logger.LogInformation(
            "ExecutionAdapterSelected AgentRunId={AgentRunId} AgentType={AgentType} Profile={Profile} Adapter={Adapter} "
                + "CorrelationId={CorrelationId}",
            spec.AgentRunId, agentType, resolution.ProfileName, adapter.Key, spec.CorrelationId);

        var effectiveGatewayKey = spec.Model.GatewayKey ?? resolution.GatewayKey;
        var effectiveSpec = spec.Model.GatewayKey != effectiveGatewayKey
            ? spec with { Model = spec.Model with { GatewayKey = effectiveGatewayKey } }
            : spec;

        return await adapter.ExecuteAsync(effectiveSpec, cancellationToken);
    }

    public async Task<AgentRunExecutionResult> ExecuteAsync(
        AgentRun agentRun,
        CancellationToken cancellationToken = default)
    {
        var agentDefinition = agentRun.AgentDefinition
            ?? throw new ArgumentException(
                "AgentRun.AgentDefinition must be loaded before execution.", nameof(agentRun));

        // Factory policy, not runtime behaviour: a disabled AgentDefinition never
        // reaches an adapter. Same message, classification and result shape the
        // runtime produced when this check lived there (before Phase C).
        if (!agentDefinition.Enabled)
        {
            var message = $"AgentDefinition '{agentDefinition.Name}' ({agentDefinition.Id}) is disabled.";

            _logger.LogWarning(
                "AgentRuntimeFailed AgentRunId={AgentRunId} CorrelationId={CorrelationId} Reason=DefinitionDisabled",
                agentRun.Id, agentRun.CorrelationId);

            return Rejected(agentRun, message, _failureClassifier.Classify(message, "validation").Category);
        }

        var resolution = _resolver.Resolve(agentDefinition.AgentType);

        if (!resolution.IsResolved)
        {
            _logger.LogWarning(
                "ExecutionAdapterResolutionFailed AgentRunId={AgentRunId} AgentType={AgentType} Profile={Profile} "
                    + "Adapter={Adapter} CorrelationId={CorrelationId} Reason={Reason}",
                agentRun.Id, agentDefinition.AgentType, resolution.ProfileName, resolution.AdapterKey,
                agentRun.CorrelationId, resolution.FailureReason);

            return Rejected(
                agentRun, resolution.FailureReason ?? "Execution adapter could not be resolved.", FailureCategory.Validation);
        }

        var adapter = resolution.Adapter!;
        var spec = BuildSpec(agentRun, agentDefinition, resolution.GatewayKey);

        if (spec.Model.ModelId is not null && !adapter.Capabilities.HasFlag(ExecutionCapabilities.FactoryRoutedModel))
        {
            var reason =
                $"Execution adapter '{adapter.Key}' (profile '{resolution.ProfileName}') cannot honour the routed model "
                + $"'{spec.Model.ModelId}'.";

            _logger.LogWarning(
                "ExecutionAdapterContractUnsatisfied AgentRunId={AgentRunId} Adapter={Adapter} CorrelationId={CorrelationId} Reason={Reason}",
                agentRun.Id, adapter.Key, agentRun.CorrelationId, reason);

            return Rejected(agentRun, reason, FailureCategory.Validation);
        }

        _logger.LogInformation(
            "ExecutionAdapterSelected AgentRunId={AgentRunId} AgentType={AgentType} Profile={Profile} Adapter={Adapter} "
                + "CorrelationId={CorrelationId}",
            agentRun.Id, agentDefinition.AgentType, resolution.ProfileName, adapter.Key, agentRun.CorrelationId);

        var outcome = await adapter.ExecuteAsync(spec, cancellationToken);

        return ToRuntimeResult(adapter.Key, outcome);
    }

    private static AgentExecutionSpec BuildSpec(AgentRun agentRun, AgentDefinition agentDefinition, string? profileGatewayKey) => new(
        AgentRunId: agentRun.Id,
        CorrelationId: agentRun.CorrelationId,
        ProjectId: agentRun.ProjectId,
        WorkItemId: agentRun.WorkItemId,
        Attempt: agentRun.Attempt,
        Iteration: agentRun.Iteration,
        Agent: new ExecutionAgent(
            agentDefinition.Id,
            agentDefinition.Name,
            agentDefinition.AgentType,
            agentDefinition.SystemPrompt),
        Instructions: agentRun.Input,
        Workspace: new ExecutionWorkspace(agentRun.WorkspacePath),
        Model: new ModelRoute(agentRun.Model, profileGatewayKey ?? agentRun.Provider),
        Timeout: agentRun.ExecutionTimeout,
        Deadline: agentRun.ExecutionDeadline);

    /// <summary>
    /// Lossless for every outcome an IAgentRuntime could previously return:
    /// Success is exactly Status == Succeeded, and FailureCategory is passed
    /// through untouched — AgentOrchestrator keeps deriving TimedOut/Cancelled
    /// from it as before. Only an adapter that reports TimedOut/Cancelled without
    /// a category gets the matching category, so the orchestrator's existing
    /// mapping still yields the same terminal AgentRunStatus.
    /// </summary>
    private static AgentRunExecutionResult ToRuntimeResult(string adapterKey, ExecutionOutcome outcome) => new(
        Success: outcome.Status == ExecutionOutcomeStatus.Succeeded,
        Output: outcome.Output,
        PromptTokens: outcome.Usage?.PromptTokens,
        CompletionTokens: outcome.Usage?.CompletionTokens,
        TotalTokens: outcome.Usage?.TotalTokens,
        DurationMs: outcome.DurationMs,
        ArtifactReferences: outcome.ArtifactReferences,
        ToolCallNames: outcome.ToolCallNames,
        ErrorMessage: outcome.ErrorMessage,
        FailureCategory: outcome.FailureCategory ?? outcome.Status switch
        {
            ExecutionOutcomeStatus.TimedOut => FailureCategory.Timeout,
            ExecutionOutcomeStatus.Cancelled => FailureCategory.Cancelled,
            _ => null
        },
        Provider: outcome.Model.GatewayKey,
        Model: outcome.Model.ModelId,
        ExecutionAdapter: adapterKey,
        ExitReason: outcome.ExitReason);

    /// <summary>No adapter ran, so no adapter key or exit reason is reported.</summary>
    private static AgentRunExecutionResult Rejected(AgentRun agentRun, string message, FailureCategory? category) => new(
        Success: false,
        Output: null,
        PromptTokens: null,
        CompletionTokens: null,
        TotalTokens: null,
        DurationMs: 0,
        ArtifactReferences: Array.Empty<string>(),
        ToolCallNames: Array.Empty<string>(),
        ErrorMessage: message,
        FailureCategory: category,
        Provider: agentRun.Provider,
        Model: agentRun.Model);
}
