using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Security;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents.Cli;

/// <summary>What one CLI adapter is configured with — the parts that differ between Claude Code and Codex.</summary>
public sealed record CliAdapterSettings(
    string ExecutablePath,
    int TimeoutSeconds,
    string CredentialSecretName,
    string CredentialEnvironmentVariable,
    IReadOnlyDictionary<string, string> Models,
    IReadOnlyList<string> ConfigDirectoryVariables,
    string ProviderName);

/// <summary>The adapter's reading of a finished CLI run, from the CLI's own structured output only.</summary>
public sealed record CliInterpretation(
    bool Succeeded,
    string? Output,
    string? Error,
    FailureCategory? FailureCategory,
    string? GatewayFailure = null,
    ExecutionUsage? Usage = null,
    string? SessionId = null,
    decimal? CostUsd = null,
    string? CostSource = null,
    int? Turns = null,
    string? ReportedModel = null,
    string? UpstreamRequestId = null);

/// <summary>
/// Per-run reader of a CLI's structured stdout. <see cref="Observe"/> sees every line live; setting
/// <see cref="AbortReason"/> makes the adapter stop the process immediately (e.g. an authentication
/// failure the CLI would otherwise retry for minutes).
/// </summary>
public abstract class CliEventStream
{
    public string? AbortReason { get; protected set; }

    public string? AbortGatewayFailure { get; protected set; }

    public abstract void Observe(string line);

    public abstract CliInterpretation Interpret(int exitCode);
}

/// <summary>
/// Phase M — the common execution boundary for local coding-agent CLIs behind <see cref="IExecutionAdapter"/>.
/// The Factory-owned lifecycle mechanics live here exactly once: deadline-derived timeout, explicit model
/// mapping (fail closed), Factory credential resolution (fail closed, environment only, never argv),
/// isolated per-run CLI home via <see cref="CliRunSandbox"/>, process launch/cancellation/tree-kill through
/// the shared <see cref="IProcessRunner"/>, credential redaction, and outcome classification. A concrete
/// adapter only builds its command line and reads its CLI's event stream.
///
/// "Exit code 0" is never success on its own: success needs the CLI's own structured result saying so —
/// and even then it only means the runtime finished; the Factory's verification and acceptance decide the
/// WorkItem. The adapter touches no Factory state, repository or artifact store.
/// </summary>
public abstract class CliExecutionAdapter : IExecutionAdapter
{
    private readonly IProcessRunner _processRunner;
    private readonly ISecretProvider _secretProvider;
    private readonly CliRuntimeOptions _cliOptions;
    private readonly ILogger _logger;

    protected CliExecutionAdapter(
        IProcessRunner processRunner,
        ISecretProvider secretProvider,
        CliRuntimeOptions cliOptions,
        ILogger logger)
    {
        _processRunner = processRunner;
        _secretProvider = secretProvider;
        _cliOptions = cliOptions;
        _logger = logger;
    }

    public abstract string Key { get; }

    /// <summary>Both CLIs run exactly the (mapped) model the Factory routed — see <see cref="CliAdapterSettings.Models"/>.</summary>
    public ExecutionCapabilities Capabilities => ExecutionCapabilities.FactoryRoutedModel;

    protected abstract CliAdapterSettings Settings { get; }

    /// <summary>Command-line arguments. Never contains the credential or the prompt (the prompt goes to stdin).</summary>
    protected abstract IReadOnlyList<string> BuildArguments(AgentExecutionSpec spec, string cliModel, string workingDirectory);

    protected abstract CliEventStream CreateEventStream();

    /// <summary>Extra, non-secret child environment (e.g. disabling auto-update/telemetry).</summary>
    protected virtual IReadOnlyDictionary<string, string>? ExtraEnvironment => null;

    public async Task<ExecutionOutcome> ExecuteAsync(AgentExecutionSpec spec, CancellationToken cancellationToken = default)
    {
        var settings = Settings;

        _logger.LogInformation(
            "CliRuntimeStarted Adapter={Adapter} AgentRunId={AgentRunId} CorrelationId={CorrelationId}",
            Key, spec.AgentRunId, spec.CorrelationId);

        if (spec.Deadline is { } deadline && deadline <= DateTimeOffset.UtcNow)
        {
            return Fail(spec, ExecutionOutcomeStatus.TimedOut, "Execution deadline was exceeded before launch.",
                FailureCategory.Timeout, "timeout");
        }

        if (string.IsNullOrWhiteSpace(spec.Model.ModelId)
            || !settings.Models.TryGetValue(spec.Model.ModelId, out var cliModel)
            || string.IsNullOrWhiteSpace(cliModel))
        {
            return Fail(spec, ExecutionOutcomeStatus.Failed,
                $"Model route rejected: routed model '{spec.Model.ModelId ?? "(none)"}' is not configured for {Key} "
                    + $"({Key}:Models). The adapter never substitutes or auto-selects a model.",
                FailureCategory.Validation, "model_rejected");
        }

        if (spec.Workspace.Path is { } workspace && !Directory.Exists(workspace))
        {
            return Fail(spec, ExecutionOutcomeStatus.Failed, $"Resolved workspace '{workspace}' does not exist.",
                FailureCategory.Validation, "workspace_missing");
        }

        if (spec.Workspace.Path is { } ws && Overlaps(ws, _cliOptions.StateRoot))
        {
            return Fail(spec, ExecutionOutcomeStatus.Failed,
                $"Security violation: the workspace and the CLI runtime state root ({_cliOptions.StateRoot}) overlap.",
                FailureCategory.Infrastructure, "workspace_rejected");
        }

        var credential = await _secretProvider.GetSecretAsync(settings.CredentialSecretName, cancellationToken);
        if (string.IsNullOrEmpty(credential))
        {
            return Fail(spec, ExecutionOutcomeStatus.Failed,
                $"Factory credential '{settings.CredentialSecretName}' could not be resolved; {Key} was not started "
                    + "(no ambient CLI login or environment credential is ever used in its place).",
                FailureCategory.Validation, "credential_missing");
        }

        using var sandbox = CliRunSandbox.Create(_cliOptions.StateRoot, spec.AgentRunId, _logger);
        var workingDirectory = spec.Workspace.Path ?? sandbox.ScratchDirectory;
        var stream = CreateEventStream();
        using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = EffectiveTimeout(spec, settings.TimeoutSeconds);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var request = new ProcessRunRequest(
            settings.ExecutablePath,
            BuildArguments(spec, cliModel, workingDirectory),
            timeout,
            workingDirectory,
            sandbox.BuildEnvironment(
                _cliOptions.InheritedEnvironmentVariables, settings.ConfigDirectoryVariables,
                settings.CredentialEnvironmentVariable, credential, ExtraEnvironment),
            StandardInput: BuildPrompt(spec),
            OnStarted: sandbox.RecordProcess,
            OnStandardOutputLine: line =>
            {
                // Output callbacks can still be draining when the abort path interprets the stream.
                lock (stream)
                {
                    stream.Observe(line);
                }

                if (stream.AbortReason is not null && !abortCts.IsCancellationRequested)
                {
                    abortCts.Cancel();
                }
            });

        ProcessRunResult result;

        try
        {
            result = await _processRunner.RunAsync(request, abortCts.Token);
        }
        catch (ProcessLaunchException ex)
        {
            return Fail(spec, ExecutionOutcomeStatus.Failed,
                $"{Key} could not be started — executable '{settings.ExecutablePath}' is missing or not runnable "
                    + $"({Key}:ExecutablePath): {Redact(ex.Message, credential)}",
                FailureCategory.Validation, "launch_failed");
        }
        catch (SecurityViolationException ex)
        {
            return Fail(spec, ExecutionOutcomeStatus.Failed, $"Security violation: {ex.Message}",
                FailureCategory.Validation, "security_violation");
        }
        catch (OperationCanceledException) when (stream.AbortReason is not null && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "CliRuntimeAborted Adapter={Adapter} AgentRunId={AgentRunId} Reason={Reason}",
                Key, spec.AgentRunId, stream.AbortReason);

            // Whatever the stream already proved (session id, an explicit upstream request id) is kept.
            CliInterpretation partial;
            lock (stream)
            {
                partial = stream.Interpret(-1);
            }

            return Fail(spec, ExecutionOutcomeStatus.Failed, Redact(stream.AbortReason, credential)!,
                FailureCategory.Provider, "aborted", stream.AbortGatewayFailure, stopwatch.ElapsedMilliseconds,
                new ExecutionRuntimeReport(Key, SessionId: partial.SessionId, StartedAt: startedAt,
                    CompletedAt: DateTimeOffset.UtcNow, UpstreamRequestId: partial.UpstreamRequestId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("CliRuntimeCancelled Adapter={Adapter} AgentRunId={AgentRunId}", Key, spec.AgentRunId);
            return Fail(spec, ExecutionOutcomeStatus.Cancelled, "Agent execution was cancelled.",
                FailureCategory.Cancelled, "cancelled", durationMs: stopwatch.ElapsedMilliseconds);
        }

        stopwatch.Stop();
        var completedAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            _logger.LogInformation(
                "CliRuntimeStderr Adapter={Adapter} AgentRunId={AgentRunId} Tail={Tail}",
                Key, spec.AgentRunId, SensitiveDataSanitizer.Sanitize(Redact(Tail(result.StandardError), credential)!));
        }

        if (result.TimedOut)
        {
            _logger.LogWarning("CliRuntimeTimedOut Adapter={Adapter} AgentRunId={AgentRunId}", Key, spec.AgentRunId);
            return Fail(spec, ExecutionOutcomeStatus.TimedOut,
                $"Execution timed out after {timeout.TotalSeconds:F0} seconds.", FailureCategory.Timeout, "timeout",
                durationMs: stopwatch.ElapsedMilliseconds);
        }

        CliInterpretation interpretation;
        lock (stream)
        {
            interpretation = stream.Interpret(result.ExitCode);
        }

        var report = new ExecutionRuntimeReport(
            Runtime: Key,
            SessionId: interpretation.SessionId,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            ExitCode: result.ExitCode,
            CostUsd: interpretation.CostUsd,
            CostSource: interpretation.CostSource,
            UpstreamRequestId: interpretation.UpstreamRequestId,
            Turns: interpretation.Turns,
            ReportedModel: interpretation.ReportedModel);

        var succeeded = result.ExitCode == 0 && interpretation.Succeeded;
        var error = succeeded
            ? null
            : Redact(interpretation.Error ?? $"{Key} exited with code {result.ExitCode} without a successful result.", credential);

        _logger.Log(
            succeeded ? LogLevel.Information : LogLevel.Warning,
            "CliRuntimeCompleted Adapter={Adapter} AgentRunId={AgentRunId} ExitCode={ExitCode} Succeeded={Succeeded} DurationMs={DurationMs}",
            Key, spec.AgentRunId, result.ExitCode, succeeded, stopwatch.ElapsedMilliseconds);

        return new ExecutionOutcome(
            succeeded ? ExecutionOutcomeStatus.Succeeded : ExecutionOutcomeStatus.Failed,
            Redact(interpretation.Output, credential),
            error,
            succeeded ? null : interpretation.FailureCategory ?? FailureCategory.Agent,
            stopwatch.ElapsedMilliseconds,
            interpretation.Usage,
            ArtifactReferences: Array.Empty<string>(),
            ToolCallNames: Array.Empty<string>(),
            Model: new ModelRoute(cliModel, settings.ProviderName),
            ExitReason: $"exit:{result.ExitCode}",
            GatewayFailure: succeeded ? null : interpretation.GatewayFailure,
            Report: report);
    }

    /// <summary>Same composition as Hermes: the definition's system prompt, then the Factory instructions.</summary>
    private static string BuildPrompt(AgentExecutionSpec spec) =>
        string.IsNullOrWhiteSpace(spec.Agent.SystemPrompt)
            ? spec.Instructions ?? string.Empty
            : $"{spec.Agent.SystemPrompt}\n\n{spec.Instructions}";

    /// <summary>The Factory deadline wins; the adapter's own ceiling only ever shortens it.</summary>
    public static TimeSpan EffectiveTimeout(AgentExecutionSpec spec, int ceilingSeconds)
    {
        var ceiling = ceilingSeconds > 0 ? TimeSpan.FromSeconds(ceilingSeconds) : TimeSpan.MaxValue;
        var factory = spec.Deadline is { } deadline
            ? deadline - DateTimeOffset.UtcNow
            : spec.Timeout ?? ceiling;

        var timeout = factory < ceiling ? factory : ceiling;
        return timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMilliseconds(1);
    }

    private static bool Overlaps(string workspace, string stateRoot)
    {
        var a = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var b = Path.GetFullPath(stateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whatever the CLI printed, the exact credential value never leaves the adapter.</summary>
    public static string? Redact(string? text, string credential) =>
        text is null || credential.Length < 8 ? text : text.Replace(credential, "[REDACTED]", StringComparison.Ordinal);

    private static string Tail(string text) => text.Length <= 2000 ? text : text[^2000..];

    private ExecutionOutcome Fail(
        AgentExecutionSpec spec,
        ExecutionOutcomeStatus status,
        string error,
        FailureCategory category,
        string exitReason,
        string? gatewayFailure = null,
        long durationMs = 0,
        ExecutionRuntimeReport? report = null)
    {
        if (status != ExecutionOutcomeStatus.Cancelled)
        {
            _logger.LogWarning(
                "CliRuntimeFailed Adapter={Adapter} AgentRunId={AgentRunId} ExitReason={ExitReason} Error={Error}",
                Key, spec.AgentRunId, exitReason, error);
        }

        return new ExecutionOutcome(
            status, Output: null, error, category, durationMs, Usage: null,
            ArtifactReferences: Array.Empty<string>(), ToolCallNames: Array.Empty<string>(),
            Model: spec.Model, ExitReason: exitReason, GatewayFailure: gatewayFailure,
            Report: report ?? new ExecutionRuntimeReport(Key));
    }

    /// <summary>"HTTP 401/403"-style authentication failures map onto the existing gateway taxonomy.</summary>
    protected static string AuthenticationFailure => nameof(ModelGatewayFailureReason.Authentication);
}
