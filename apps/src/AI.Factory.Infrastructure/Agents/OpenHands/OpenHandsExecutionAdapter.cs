using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Gateway;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents.OpenHands;

/// <summary>
/// Phase E — OpenHands Execution Adapter.
/// Integrates OpenHands Agent Server behind the provider-neutral IExecutionAdapter contract.
/// Executes an AgentRun against an isolated OpenHands runtime, honours authoritative deadlines,
/// handles cancellation, maps exit status and token usage, and guarantees deterministic container cleanup.
/// </summary>
public sealed class OpenHandsExecutionAdapter : IExecutionAdapter
{
    public const string AdapterKey = "OpenHands";

    /// <summary>
    /// Phase L — container-local root for the Agent Server's conversation and bash-event state:
    /// outside the /workspace bind mount, writable by the image's non-root user, removed with the container.
    /// </summary>
    public const string RuntimeStateRoot = "/tmp/aif-openhands";

    private readonly OpenHandsOptions _options;
    private readonly IProcessRunner _processRunner;
    private readonly IContainerLifecycleManager _lifecycleManager;
    private readonly IFailureClassifier _failureClassifier;
    private readonly ISecretProvider? _secretProvider;
    private readonly IOpenHandsClientFactory _clientFactory;
    private readonly ModelGatewayResolver? _gatewayResolver;
    private readonly ILogger<OpenHandsExecutionAdapter> _logger;

    public OpenHandsExecutionAdapter(
        OpenHandsOptions options,
        IProcessRunner processRunner,
        IContainerLifecycleManager lifecycleManager,
        IFailureClassifier failureClassifier,
        ILogger<OpenHandsExecutionAdapter> logger,
        IOpenHandsClientFactory? clientFactory = null,
        ISecretProvider? secretProvider = null,
        ModelGatewayResolver? gatewayResolver = null)
    {
        _options = options;
        _processRunner = processRunner;
        _lifecycleManager = lifecycleManager;
        _failureClassifier = failureClassifier;
        _logger = logger;
        _clientFactory = clientFactory ?? new DefaultOpenHandsClientFactory();
        _secretProvider = secretProvider;
        _gatewayResolver = gatewayResolver;
    }

    public string Key => AdapterKey;

    public ExecutionCapabilities Capabilities => ExecutionCapabilities.FactoryRoutedModel;

    public async Task<ExecutionOutcome> ExecuteAsync(
        AgentExecutionSpec spec,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "OpenHandsRuntimeStarted AgentRunId={AgentRunId} AgentDefinitionId={AgentDefinitionId} CorrelationId={CorrelationId}",
            spec.AgentRunId, spec.Agent.DefinitionId, spec.CorrelationId);

        if (spec.Deadline.HasValue && spec.Deadline.Value <= DateTimeOffset.UtcNow)
        {
            _logger.LogWarning(
                "OpenHandsRuntimeTimedOut AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs=0 Reason=DeadlineAlreadyExceeded",
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

        if (!string.IsNullOrWhiteSpace(spec.Model.ModelId) && ModelRoutingOptions.IsAutoOrImplicitModel(spec.Model.ModelId))
        {
            var msg = $"Model '{spec.Model.ModelId}' is invalid auto or implicit model. Explicit non-auto model is required.";
            _logger.LogError("OpenHandsExecutionAdapter rejected invalid model: {Model}", spec.Model.ModelId);
            return Outcome(
                spec,
                ExecutionOutcomeStatus.Failed,
                output: null,
                error: msg,
                category: FailureCategory.Model,
                0,
                ExitReasons.Error);
        }

        var stopwatch = Stopwatch.StartNew();
        var workspacePath = ResolveWorkspacePath(spec);
        var containerSpawned = false;
        var launchCompleted = false;
        var launchRejected = false;
        string? activeConversationId = null;
        IOpenHandsClient? client = null;

        try
        {
            string serverUrl;
            string? sessionApiKey;

            if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
            {
                serverUrl = _options.BaseUrl.TrimEnd('/');
                sessionApiKey = _options.SessionApiKey;
            }
            else
            {
                var ephemeralPort = GetFreePort();
                sessionApiKey = !string.IsNullOrWhiteSpace(_options.SessionApiKey)
                    ? _options.SessionApiKey
                    : Guid.NewGuid().ToString("N");

                serverUrl = $"http://127.0.0.1:{ephemeralPort}";

                // Phase L — set before the launch: `docker run -d` can create the container and still
                // fail (port bind) or be cancelled mid-flight. Cleanup is `docker rm -f` of this run's
                // own deterministic name (idempotent): it can only ever remove a container of this AgentRun.
                containerSpawned = true;
                try
                {
                    await LaunchContainerAsync(spec, workspacePath, ephemeralPort, sessionApiKey, cancellationToken);
                    launchCompleted = true;
                }
                catch (ContainerLaunchRejectedException)
                {
                    // Docker answered with a definite error: nothing is still being created.
                    launchRejected = true;
                    throw;
                }
            }

            client = _clientFactory.CreateClient(serverUrl, sessionApiKey);

            // Await readiness
            await WaitForServerReadyAsync(client, spec.Deadline, cancellationToken);

            // Configure git safe directory in container
            if (containerSpawned)
            {
                await ConfigureGitSafeDirectoryAsync(spec.AgentRunId, cancellationToken);
            }

            // Resolve model & API key
            var (modelId, apiKey, providerBaseUrl) = await ResolveModelAndApiKeyAsync(spec, cancellationToken);

            // Create conversation
            var createRequest = new OpenHandsCreateConversationRequest
            {
                Workspace = new OpenHandsWorkspaceDto
                {
                    Kind = "LocalWorkspace",
                    WorkingDir = "/workspace"
                },
                AgentSettings = new OpenHandsAgentSettingsDto
                {
                    AgentKind = "openhands",
                    Agent = "CodeActAgent",
                    Llm = new OpenHandsLlmDto
                    {
                        Model = modelId,
                        ApiKey = apiKey,
                        BaseUrl = providerBaseUrl
                    }
                }
            };

            var conv = await client.CreateConversationAsync(createRequest, cancellationToken);
            activeConversationId = conv.Id;

            // Submit prompt and trigger execution
            var prompt = BuildPrompt(spec);
            await client.SendMessageAsync(activeConversationId, prompt, run: true, cancellationToken);

            // Poll until terminal or timeout/cancellation
            var outcome = await PollExecutionAsync(client, activeConversationId, spec, stopwatch, cancellationToken);
            return outcome;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogInformation(
                "OpenHandsRuntimeCancelled AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                spec.AgentRunId, spec.CorrelationId, stopwatch.ElapsedMilliseconds);

            if (client is not null && activeConversationId is not null)
            {
                await InterruptQuietlyAsync(client, activeConversationId);
            }

            return Outcome(
                spec,
                ExecutionOutcomeStatus.Cancelled,
                output: null,
                error: "Agent execution was cancelled.",
                _failureClassifier.Classify(ex).Category,
                stopwatch.ElapsedMilliseconds,
                ExitReasons.Cancelled);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Phase K — an Agent Server error body can echo the conversation request, which carries
            // the provider API key; the raw message is never logged or persisted.
            var error = ModelGatewayFailureClassifier.SanitizeUpstreamMessage(ex.Message);
            _logger.LogError(
                "OpenHandsRuntimeFailed AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs={DurationMs} ExceptionType={ExceptionType} Error={Error}",
                spec.AgentRunId, spec.CorrelationId, stopwatch.ElapsedMilliseconds, ex.GetType().Name, error);

            var isTimeout = ex is TimeoutException ||
                ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);

            var gatewayReason = ModelGatewayFailureClassifier.FromRuntimeMessage(error);
            string hint = isTimeout ? "timeout" : "agent";
            if (gatewayReason != null)
            {
                hint = gatewayReason == ModelGatewayFailureReason.ModelUnavailable ? "model" : "provider";
            }
            else if (ex is ContainerLaunchRejectedException || error.Contains("docker", StringComparison.OrdinalIgnoreCase) || error.Contains("port", StringComparison.OrdinalIgnoreCase))
            {
                hint = "infrastructure";
            }

            var classification = _failureClassifier.Classify(error, hint);

            return Outcome(
                spec,
                isTimeout ? ExecutionOutcomeStatus.TimedOut : ExecutionOutcomeStatus.Failed,
                output: null,
                error: error,
                classification.Category,
                stopwatch.ElapsedMilliseconds,
                isTimeout ? ExitReasons.Timeout : ExitReasons.Error);
        }
        finally
        {
            if (containerSpawned)
            {
                try
                {
                    // Phase O — a launch that was interrupted (cancelled, timed out) may still be completing inside the
                    // Docker daemon: reconcile for a bounded window instead of removing once and finding nothing.
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    if (launchCompleted || launchRejected)
                    {
                        await _lifecycleManager.EnsureCleanupAsync(spec.AgentRunId, cleanupCts.Token);
                    }
                    else
                    {
                        var cleanup = await _lifecycleManager.ReconcileCleanupAsync(spec.AgentRunId, cleanupCts.Token);
                        _logger.LogInformation(
                            "OpenHandsContainerCleanup AgentRunId={AgentRunId} Outcome={Outcome}", spec.AgentRunId, cleanup);
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to clean up OpenHands container for AgentRun {AgentRunId}.", spec.AgentRunId);
                }
            }
        }
    }

    private async Task<ExecutionOutcome> PollExecutionAsync(
        IOpenHandsClient client,
        string conversationId,
        AgentExecutionSpec spec,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1.0, _options.PollIntervalSeconds));
        var hasSeenRunning = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (spec.Deadline.HasValue && DateTimeOffset.UtcNow >= spec.Deadline.Value)
            {
                stopwatch.Stop();
                _logger.LogWarning(
                    "OpenHandsRuntimeTimedOut AgentRunId={AgentRunId} DurationMs={DurationMs} Reason=AuthoritativeDeadlineExceeded",
                    spec.AgentRunId, stopwatch.ElapsedMilliseconds);

                await InterruptQuietlyAsync(client, conversationId);

                return Outcome(
                    spec,
                    ExecutionOutcomeStatus.TimedOut,
                    output: null,
                    error: "Authoritative execution deadline was exceeded.",
                    _failureClassifier.Classify("Authoritative execution deadline exceeded.", "timeout").Category,
                    stopwatch.ElapsedMilliseconds,
                    ExitReasons.Timeout);
            }

            await Task.Delay(pollInterval, cancellationToken);

            var conv = await client.GetConversationAsync(conversationId, cancellationToken);
            var status = conv.ExecutionStatus?.ToLowerInvariant() ?? string.Empty;

            if (status == "running")
            {
                hasSeenRunning = true;
            }
            else if (status is "finished" or "idle" && hasSeenRunning)
            {
                stopwatch.Stop();
                var finalResponse = await client.GetFinalResponseAsync(conversationId, cancellationToken);
                var usage = ExtractUsage(conv);

                _logger.LogInformation(
                    "OpenHandsRuntimeCompleted AgentRunId={AgentRunId} DurationMs={DurationMs}",
                    spec.AgentRunId, stopwatch.ElapsedMilliseconds);

                return Outcome(
                    spec,
                    ExecutionOutcomeStatus.Succeeded,
                    output: finalResponse,
                    error: null,
                    category: null,
                    stopwatch.ElapsedMilliseconds,
                    ExitReasons.Completed,
                    usage: usage);
            }
            else if (status is "error" or "stuck")
            {
                stopwatch.Stop();
                var errorMessage = $"OpenHands conversation transitioned to error state: {status}";
                _logger.LogWarning(
                    "OpenHandsRuntimeFailed AgentRunId={AgentRunId} Status={Status} DurationMs={DurationMs}",
                    spec.AgentRunId, status, stopwatch.ElapsedMilliseconds);

                var classification = _failureClassifier.Classify(errorMessage, "agent");
                return Outcome(
                    spec,
                    ExecutionOutcomeStatus.Failed,
                    output: null,
                    error: errorMessage,
                    classification.Category,
                    stopwatch.ElapsedMilliseconds,
                    ExitReasons.Error);
            }
            else if (status is "paused")
            {
                stopwatch.Stop();
                return Outcome(
                    spec,
                    ExecutionOutcomeStatus.Cancelled,
                    output: null,
                    error: "OpenHands conversation was paused/interrupted.",
                    _failureClassifier.Classify("OpenHands conversation paused.", "cancelled").Category,
                    stopwatch.ElapsedMilliseconds,
                    ExitReasons.Cancelled);
            }
        }
    }

    private async Task LaunchContainerAsync(
        AgentExecutionSpec spec,
        string workspacePath,
        int port,
        string sessionApiKey,
        CancellationToken cancellationToken)
    {
        var containerName = DockerContainerNaming.GetContainerName(spec.AgentRunId);
        var labels = DockerContainerNaming.GetExecutionLabels(spec.AgentRunId, spec.ProjectId, adapter: Key);

        var args = new List<string>
        {
            "run", "-d",
            "--name", containerName,
            "-p", $"127.0.0.1:{port}:8000"
        };

        foreach (var (k, v) in labels)
        {
            args.Add("--label");
            args.Add($"{k}={v}");
        }

        args.Add("--network");
        args.Add(_options.NetworkMode);

        // Phase K — passed by NAME; the value lives only in the docker CLI's controlled environment
        // below, never on its command line (and the CLI no longer inherits the Worker's environment).
        args.Add("-e");
        args.Add("SESSION_API_KEY");

        args.Add("-e");
        args.Add("OPENHANDS_SUPPRESS_BANNER=1");

        // Phase L — the Agent Server's own state stays out of the Project workspace. Its defaults
        // (relative to its working directory "/") are workspace/conversations and workspace/bash_events,
        // i.e. inside the bind mount: conversation state incl. the encrypted LLM key, event and
        // terminal history (verified live on 1.49.4). Relocated into the container's writable layer,
        // it is destroyed with the container (`docker rm -f`) and never reaches git or artifacts.
        args.Add("-e");
        args.Add($"OH_CONVERSATIONS_PATH={RuntimeStateRoot}/conversations");
        args.Add("-e");
        args.Add($"OH_BASH_EVENTS_DIR={RuntimeStateRoot}/bash_events");

        args.Add("-v");
        args.Add($"{workspacePath}:/workspace");

        args.Add(_options.DockerImage);

        if (!string.IsNullOrWhiteSpace(_options.ImportModules))
        {
            args.Add("--import-modules");
            args.Add(_options.ImportModules);
        }

        var environment = new Dictionary<string, string> { ["SESSION_API_KEY"] = sessionApiKey };
        if (Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } path)
        {
            environment["PATH"] = path;
        }

        var processRequest = new ProcessRunRequest(
            "docker",
            args,
            TimeSpan.FromSeconds(30),
            WorkingDirectory: workspacePath,
            EnvironmentVariables: environment);

        var result = await _processRunner.RunAsync(processRequest, cancellationToken);
        if (result.ExitCode != 0)
        {
            var message = $"Failed to start OpenHands container '{containerName}': {result.StandardError}";

            // A timed-out launch is NOT a definite answer from Docker (the client was killed mid-request).
            throw result.TimedOut
                ? new InvalidOperationException(message)
                : new ContainerLaunchRejectedException(message);
        }
    }

    /// <summary>`docker run -d` returned a non-zero exit code of its own accord, so the daemon has nothing left in flight.</summary>
    private sealed class ContainerLaunchRejectedException : InvalidOperationException
    {
        public ContainerLaunchRejectedException(string message) : base(message)
        {
        }
    }

    private async Task WaitForServerReadyAsync(
        IOpenHandsClient client,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.StartupTimeoutSeconds);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.HasValue && DateTimeOffset.UtcNow >= deadline.Value)
            {
                throw new TimeoutException("Authoritative deadline reached while waiting for OpenHands server startup.");
            }

            if (await client.CheckHealthAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException($"OpenHands server failed to become healthy within {timeout.TotalSeconds} seconds.");
    }

    private async Task ConfigureGitSafeDirectoryAsync(Guid agentRunId, CancellationToken cancellationToken)
    {
        var containerName = DockerContainerNaming.GetContainerName(agentRunId);
        var req = new ProcessRunRequest(
            "docker",
            new[] { "exec", containerName, "git", "config", "--global", "--add", "safe.directory", "*" },
            TimeSpan.FromSeconds(10));

        await _processRunner.RunAsync(req, cancellationToken);
    }

    /// <summary>
    /// Phase K — the routed gateway is the only source of the LiteLLM endpoint, model route and
    /// credential reference; the credential VALUE comes only through <see cref="ISecretProvider"/>.
    /// No resolver, an unresolved gateway or a missing secret fails the run before any model call.
    /// </summary>
    private async Task<(string ModelId, string ApiKey, string BaseUrl)> ResolveModelAndApiKeyAsync(
        AgentExecutionSpec spec,
        CancellationToken cancellationToken)
    {
        if (_gatewayResolver is null)
        {
            throw new InvalidOperationException("OpenHands requires a model gateway resolver; no route can be honoured without one.");
        }

        var resolution = _gatewayResolver.Resolve(spec.Model);
        if (!resolution.IsResolved || resolution.Gateway is null)
        {
            throw new InvalidOperationException(
                $"Model gateway route rejected: {resolution.FailureReason ?? "Model gateway could not be resolved."}");
        }

        if (string.IsNullOrWhiteSpace(spec.Model.ModelId) || ModelRoutingOptions.IsAutoOrImplicitModel(spec.Model.ModelId))
        {
            throw new InvalidOperationException(
                $"Model '{spec.Model.ModelId}' is invalid, auto, or implicit. Explicit non-auto model is required.");
        }

        var transport = resolution.Gateway.GetTransportInfo();
        var modelId = resolution.Gateway.FormatModelIdentifier(spec.Model.ModelId);

        var apiKey = _secretProvider is not null && !string.IsNullOrWhiteSpace(transport.ApiKeySecretName)
            ? await _secretProvider.GetSecretAsync(transport.ApiKeySecretName, cancellationToken)
            : null;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key could be resolved for model gateway '{resolution.GatewayKey}' ({transport.ApiKeySecretName}).");
        }

        return (modelId, apiKey, transport.BaseUrl);
    }

    private string ResolveWorkspacePath(AgentExecutionSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.Workspace.Path))
        {
            if (!Directory.Exists(spec.Workspace.Path))
            {
                throw new DirectoryNotFoundException($"Workspace directory does not exist: {spec.Workspace.Path}");
            }
            return spec.Workspace.Path;
        }

        var scratch = Path.Combine(_options.WorkspaceRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        return scratch;
    }

    private static string BuildPrompt(AgentExecutionSpec spec)
    {
        return string.IsNullOrWhiteSpace(spec.Agent.SystemPrompt)
            ? spec.Instructions ?? string.Empty
            : $"{spec.Agent.SystemPrompt}\n\n{spec.Instructions}";
    }

    private static ExecutionUsage? ExtractUsage(OpenHandsConversationInfo conv)
    {
        if (conv.Stats?.UsageToMetrics is null)
        {
            return null;
        }

        foreach (var metric in conv.Stats.UsageToMetrics.Values)
        {
            if (metric.AccumulatedTokenUsage is { } tokens &&
                (tokens.PromptTokens.HasValue || tokens.CompletionTokens.HasValue))
            {
                // Phase J — an unreported count stays unknown (null), never zero; the total is
                // only derived when both parts were actually reported.
                var prompt = tokens.PromptTokens;
                var completion = tokens.CompletionTokens;
                return new ExecutionUsage(prompt, completion, prompt + completion);
            }
        }

        return null;
    }

    private static async Task InterruptQuietlyAsync(IOpenHandsClient client, string conversationId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.InterruptAsync(conversationId, cts.Token);
        }
        catch
        {
            // Best effort interrupt on cancel/timeout
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ExecutionOutcome Outcome(
        AgentExecutionSpec spec,
        ExecutionOutcomeStatus status,
        string? output,
        string? error,
        FailureCategory? category,
        long durationMs,
        string exitReason,
        ExecutionUsage? usage = null) => new(
            status,
            output,
            error,
            category,
            durationMs,
            Usage: usage,
            ArtifactReferences: Array.Empty<string>(),
            ToolCallNames: Array.Empty<string>(),
            Model: spec.Model,
            ExitReason: exitReason);

    public static class ExitReasons
    {
        public const string Completed = "exit:0";
        public const string Timeout = "timeout";
        public const string Cancelled = "cancelled";
        public const string Error = "error";
    }
}
