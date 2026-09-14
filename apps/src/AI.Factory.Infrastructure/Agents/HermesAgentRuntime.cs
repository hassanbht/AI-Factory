using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// The first real <see cref="IAgentRuntime"/> implementation: executes a
/// canonical <see cref="AgentRun"/> by delegating to the existing Hermes
/// process-execution pipeline (<see cref="IAgentExecutor"/> — resilience/
/// circuit-breaker/bulkhead/retry already applied by its registered
/// decorator — which in turn uses <see cref="IProcessRunner"/> /
/// <see cref="SystemProcessRunner"/> unchanged). This class adds no new
/// process-management logic; it only maps AgentRun/AgentDefinition into an
/// <see cref="AgentExecutionRequest"/> and maps the resulting
/// <see cref="AgentExecutionResult"/> back into the canonical
/// <see cref="AgentRunExecutionResult"/> contract.
///
/// It does NOT own or mutate AgentRun.Status — that belongs to the future
/// orchestration layer (Phase 18.1.5). It does NOT create or update
/// HermesExecution rows — that legacy record stays owned by the existing
/// AutonomousCodingOrchestrator/WorkItem pipeline; coupling this new runtime
/// to it would recreate the exact responsibility mixing Phase 18.1.2 split
/// apart.
/// </summary>
public sealed class HermesAgentRuntime : IAgentRuntime
{
    private readonly IAgentExecutor _agentExecutor;
    private readonly IFailureClassifier _failureClassifier;
    private readonly IArtifactRecorder _artifactRecorder;
    private readonly IArtifactRepository _artifactRepository;
    private readonly ILogger<HermesAgentRuntime> _logger;

    public HermesAgentRuntime(
        IAgentExecutor agentExecutor,
        IFailureClassifier failureClassifier,
        IArtifactRecorder artifactRecorder,
        IArtifactRepository artifactRepository,
        ILogger<HermesAgentRuntime> logger)
    {
        _agentExecutor = agentExecutor;
        _failureClassifier = failureClassifier;
        _artifactRecorder = artifactRecorder;
        _artifactRepository = artifactRepository;
        _logger = logger;
    }

    public async Task<AgentRunExecutionResult> ExecuteAsync(
        AgentRun agentRun,
        CancellationToken cancellationToken = default)
    {
        var agentDefinition = agentRun.AgentDefinition
            ?? throw new ArgumentException(
                "AgentRun.AgentDefinition must be loaded before execution.", nameof(agentRun));

        _logger.LogInformation(
            "AgentRuntimeStarted AgentRunId={AgentRunId} AgentDefinitionId={AgentDefinitionId} CorrelationId={CorrelationId}",
            agentRun.Id, agentRun.AgentDefinitionId, agentRun.CorrelationId);

        if (!agentDefinition.Enabled)
        {
            var message = $"AgentDefinition '{agentDefinition.Name}' ({agentDefinition.Id}) is disabled.";

            _logger.LogWarning(
                "AgentRuntimeFailed AgentRunId={AgentRunId} CorrelationId={CorrelationId} Reason=DefinitionDisabled",
                agentRun.Id, agentRun.CorrelationId);

            var classification = _failureClassifier.Classify(message, "validation");

            return new AgentRunExecutionResult(
                Success: false,
                Output: null,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                DurationMs: 0,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                ErrorMessage: message,
                FailureCategory: classification.Category,
                Provider: agentRun.Provider,
                Model: agentRun.Model);
        }

        var prompt = BuildPrompt(agentDefinition, agentRun);
        var request = new AgentExecutionRequest(
            Agent: agentDefinition.Name,
            Model: agentRun.Model,
            Prompt: prompt,
            // Phase 18.14 — the Factory-resolved workspace (AgentOrchestrator
            // -> IWorkspaceResolver), never chosen here. Empty only for the
            // one legitimate remaining case: a Planner-only AgentRun with no
            // WorkItemId, which HermesAgentExecutor falls back to its own
            // fresh scratch folder for.
            WorkingDirectory: agentRun.WorkspacePath ?? string.Empty);

        var stopwatch = Stopwatch.StartNew();

        AgentExecutionResult result;

        try
        {
            result = await _agentExecutor.ExecuteAsync(request, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            _logger.LogInformation(
                "AgentRuntimeCancelled AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                agentRun.Id, agentRun.CorrelationId, stopwatch.ElapsedMilliseconds);

            var classification = _failureClassifier.Classify(ex);

            return new AgentRunExecutionResult(
                Success: false,
                Output: null,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                DurationMs: stopwatch.ElapsedMilliseconds,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                ErrorMessage: "Agent execution was cancelled.",
                FailureCategory: classification.Category,
                Provider: agentRun.Provider,
                Model: agentRun.Model);
        }

        stopwatch.Stop();

        if (result.Success)
        {
            _logger.LogInformation(
                "AgentRuntimeCompleted AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                agentRun.Id, agentRun.CorrelationId, result.DurationMs);

            var artifactReferences = await RecordArtifactsAsync(agentRun, result, cancellationToken);

            return new AgentRunExecutionResult(
                Success: true,
                Output: result.Output,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                DurationMs: result.DurationMs,
                ArtifactReferences: artifactReferences,
                ToolCallNames: Array.Empty<string>(),
                ErrorMessage: null,
                FailureCategory: null,
                Provider: agentRun.Provider,
                Model: agentRun.Model);
        }

        var errorMessage = result.Error ?? "Agent execution failed with no further detail.";
        var isTimeout = errorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase);

        var failureClassification = _failureClassifier.Classify(errorMessage, isTimeout ? "timeout" : "agent");

        if (isTimeout)
        {
            _logger.LogWarning(
                "AgentRuntimeTimedOut AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                agentRun.Id, agentRun.CorrelationId, result.DurationMs);
        }
        else
        {
            _logger.LogWarning(
                "AgentRuntimeFailed AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                agentRun.Id, agentRun.CorrelationId, result.DurationMs);
        }

        return new AgentRunExecutionResult(
            Success: false,
            Output: result.Output,
            PromptTokens: null,
            CompletionTokens: null,
            TotalTokens: null,
            DurationMs: result.DurationMs,
            ArtifactReferences: Array.Empty<string>(),
            ToolCallNames: Array.Empty<string>(),
            ErrorMessage: errorMessage,
            FailureCategory: failureClassification.Category,
            Provider: agentRun.Provider,
            Model: agentRun.Model);
    }

    private static string BuildPrompt(AgentDefinition agentDefinition, AgentRun agentRun)
    {
        return string.IsNullOrWhiteSpace(agentDefinition.SystemPrompt)
            ? agentRun.Input ?? string.Empty
            : $"{agentDefinition.SystemPrompt}\n\n{agentRun.Input}";
    }

    /// <summary>
    /// Best-effort: only possible when the AgentRun carries both ProjectId
    /// and WorkItemId (the existing IArtifactRecorder is keyed by both).
    /// An AgentRun with neither — the common case until orchestration wires
    /// them in Phase 18.1.5 — simply returns no artifact references, which
    /// is correct, not a failure.
    /// </summary>
    private async Task<IReadOnlyList<string>> RecordArtifactsAsync(
        AgentRun agentRun,
        AgentExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (agentRun.ProjectId is not { } projectId ||
            agentRun.WorkItemId is not { } workItemId ||
            string.IsNullOrWhiteSpace(result.WorkspacePath))
        {
            return Array.Empty<string>();
        }

        await _artifactRecorder.RecordWorkspaceArtifactsAsync(
            projectId, workItemId, result.WorkspacePath, cancellationToken);

        var artifacts = await _artifactRepository.GetByWorkItemIdAsync(workItemId, cancellationToken);

        return artifacts
            .Where(a => a.ProjectId == projectId)
            .Select(a => a.Id.ToString())
            .ToArray();
    }
}
