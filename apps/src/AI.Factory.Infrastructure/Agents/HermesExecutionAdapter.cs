using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// Execution-adapter migration Phase C — the "Hermes" <see cref="IExecutionAdapter"/>
/// and the single owner of Hermes execution for AgentRuns. It maps the
/// provider-neutral <see cref="AgentExecutionSpec"/> to a Hermes request (prompt =
/// system prompt + instructions, agent name, routed model, Factory-resolved
/// workspace), runs it through the registered <see cref="IAgentExecutor"/> chain
/// (ResilienceAgentExecutorDecorator -> HermesAgentExecutor, which owns the docker
/// run arguments, -z/-m/--provider, environment and API-key injection, workspace
/// mount, process timeout and output truncation), and interprets the result:
/// timeout, cancellation, failure classification and exit reason.
///
/// Behaviour is moved unchanged from the former HermesAgentRuntime. What it does
/// NOT do, by design: decide whether an AgentDefinition may run (the dispatcher
/// does), record workspace artifacts (AgentOrchestrator does), touch the database
/// or AgentRun/WorkItem/Project state, or choose a workspace or model itself.
/// </summary>
public sealed class HermesExecutionAdapter : IExecutionAdapter
{
    public const string AdapterKey = "Hermes";

    private readonly IAgentExecutor _agentExecutor;
    private readonly IFailureClassifier _failureClassifier;
    private readonly ILogger<HermesExecutionAdapter> _logger;

    public HermesExecutionAdapter(
        IAgentExecutor agentExecutor,
        IFailureClassifier failureClassifier,
        ILogger<HermesExecutionAdapter> logger)
    {
        _agentExecutor = agentExecutor;
        _failureClassifier = failureClassifier;
        _logger = logger;
    }

    public string Key => AdapterKey;

    // Hermes has always received AgentRun.Model as its -m value.
    public ExecutionCapabilities Capabilities => ExecutionCapabilities.FactoryRoutedModel;

    public async Task<ExecutionOutcome> ExecuteAsync(
        AgentExecutionSpec spec,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "AgentRuntimeStarted AgentRunId={AgentRunId} AgentDefinitionId={AgentDefinitionId} CorrelationId={CorrelationId}",
            spec.AgentRunId, spec.Agent.DefinitionId, spec.CorrelationId);

        if (spec.Deadline.HasValue && spec.Deadline.Value <= DateTimeOffset.UtcNow)
        {
            _logger.LogWarning(
                "AgentRuntimeTimedOut AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs=0 Reason=DeadlineAlreadyExceeded",
                spec.AgentRunId, spec.CorrelationId);

            return Outcome(
                spec,
                ExecutionOutcomeStatus.TimedOut,
                output: null,
                error: "Execution deadline was exceeded before launch.",
                _failureClassifier.Classify("Execution deadline was exceeded.", "timeout").Category,
                0,
                ExitReasons.Timeout);
        }

        // WorkingDirectory is the workspace AgentOrchestrator resolved for this run,
        // never chosen here. Empty only for a Planner-only AgentRun with no
        // WorkItemId, which HermesAgentExecutor falls back to its own fresh
        // scratch folder for.
        var request = new AgentExecutionRequest(
            Agent: spec.Agent.Name,
            Model: spec.Model.ModelId,
            Prompt: BuildPrompt(spec),
            WorkingDirectory: spec.Workspace.Path ?? string.Empty,
            AgentRunId: spec.AgentRunId,
            ProjectId: spec.ProjectId,
            Adapter: Key,
            Timeout: spec.Timeout,
            Deadline: spec.Deadline,
            Gateway: spec.Model.GatewayKey);

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
                spec.AgentRunId, spec.CorrelationId, stopwatch.ElapsedMilliseconds);

            return Outcome(
                spec,
                ExecutionOutcomeStatus.Cancelled,
                output: null,
                error: "Agent execution was cancelled.",
                _failureClassifier.Classify(ex).Category,
                stopwatch.ElapsedMilliseconds,
                ExitReasons.Cancelled);
        }

        stopwatch.Stop();
        var durationMs = result.DurationMs > 0 ? result.DurationMs : stopwatch.ElapsedMilliseconds;

        if (result.Success)
        {
            _logger.LogInformation(
                "AgentRuntimeCompleted AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                spec.AgentRunId, spec.CorrelationId, durationMs);

            // HermesAgentExecutor reports success only for exit code 0 without a failed run report.
            return Outcome(
                spec,
                ExecutionOutcomeStatus.Succeeded,
                result.Output,
                error: null,
                category: null,
                durationMs,
                ExitReasons.Exit(result.ExitCode ?? 0),
                result.Usage,
                report: result.Report);
        }

        var errorMessage = result.Error ?? "Agent execution failed with no further detail.";
        var gatewayFailure = GatewayFailureReason(result, errorMessage);

        // A run Hermes reported as failed (exit code 0) carries the upstream failure text, so
        // "timeout" words in it mean nothing; only a gateway-stated timeout (HTTP 408/504) does.
        var isTimeout = result.ExitCode == 0
            ? gatewayFailure == nameof(Application.Abstractions.Gateway.ModelGatewayFailureReason.Timeout)
            : errorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase);

        var failureClassification = _failureClassifier.Classify(errorMessage, isTimeout ? "timeout" : "agent");

        if (isTimeout)
        {
            _logger.LogWarning(
                "AgentRuntimeTimedOut AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                spec.AgentRunId, spec.CorrelationId, durationMs);
        }
        else
        {
            _logger.LogWarning(
                "AgentRuntimeFailed AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                spec.AgentRunId, spec.CorrelationId, durationMs);
        }

        return Outcome(
            spec,
            isTimeout ? ExecutionOutcomeStatus.TimedOut : ExecutionOutcomeStatus.Failed,
            result.Output,
            errorMessage,
            failureClassification.Category,
            durationMs,
            result.ExitCode is { } exitCode
                ? ExitReasons.Exit(exitCode)
                : isTimeout ? ExitReasons.Timeout : ExitReasons.Error,
            result.Usage,
            gatewayFailure,
            result.Report);
    }

    /// <summary>
    /// The gateway reason Hermes stated (an explicit <c>HTTP nnn</c>). For a run Hermes itself
    /// reported as failed (exit code 0) the whole output is its failure summary, so it is read
    /// too — the bounded error text can lose the status line behind start-up output. Phase L: only
    /// then — on an unproven or incomplete exit-0 run the output is the agent's own text, and an
    /// "HTTP 429" in it is not a gateway failure.
    /// </summary>
    private static string? GatewayFailureReason(AgentExecutionResult result, string errorMessage) =>
        (ModelGatewayFailureClassifier.FromRuntimeMessage(errorMessage)
            ?? (errorMessage.StartsWith(HermesAgentExecutor.RuntimeReportedFailurePrefix, StringComparison.Ordinal)
                ? ModelGatewayFailureClassifier.FromRuntimeMessage(result.Output)
                : null))
        ?.ToString();

    private static string BuildPrompt(AgentExecutionSpec spec)
    {
        return string.IsNullOrWhiteSpace(spec.Agent.SystemPrompt)
            ? spec.Instructions ?? string.Empty
            : $"{spec.Agent.SystemPrompt}\n\n{spec.Instructions}";
    }

    /// <summary>
    /// Hermes reports no tool calls or artifact references of its own, and runs with the
    /// model/gateway the Factory routed. Token usage is whatever its run report contained
    /// (null when none); the gateway failure reason only what its failure text states.
    /// </summary>
    private static ExecutionOutcome Outcome(
        AgentExecutionSpec spec,
        ExecutionOutcomeStatus status,
        string? output,
        string? error,
        Domain.Enums.FailureCategory? category,
        long durationMs,
        string exitReason,
        ExecutionUsage? usage = null,
        string? gatewayFailure = null,
        ExecutionRuntimeReport? report = null) => new(
            status,
            output,
            error,
            category,
            durationMs,
            Usage: usage,
            ArtifactReferences: Array.Empty<string>(),
            ToolCallNames: Array.Empty<string>(),
            Model: spec.Model,
            ExitReason: exitReason,
            GatewayFailure: gatewayFailure,
            Report: report);

    /// <summary>
    /// Machine-readable exit reasons: <c>exit:N</c> when the Hermes process ran to
    /// completion with exit code N; otherwise <c>timeout</c>, <c>cancelled</c>, or
    /// <c>error</c> (no process exit was observed — launch failure, missing
    /// workspace, security refusal, retries exhausted, circuit open, bulkhead full).
    /// </summary>
    public static class ExitReasons
    {
        public const string Timeout = "timeout";
        public const string Cancelled = "cancelled";
        public const string Error = "error";

        public static string Exit(int exitCode) => $"exit:{exitCode}";
    }
}
