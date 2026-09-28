using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Gateway;
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

        var stopwatch = Stopwatch.StartNew();
        var workspacePath = ResolveWorkspacePath(spec);
        var containerSpawned = false;
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

                await LaunchContainerAsync(spec, workspacePath, ephemeralPort, sessionApiKey, cancellationToken);
                containerSpawned = true;
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
            _logger.LogError(ex,
                "OpenHandsRuntimeFailed AgentRunId={AgentRunId} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                spec.AgentRunId, spec.CorrelationId, stopwatch.ElapsedMilliseconds);

            var isTimeout = ex is TimeoutException ||
                ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);

            var classification = _failureClassifier.Classify(ex.Message, isTimeout ? "timeout" : "agent");

            return Outcome(
                spec,
                isTimeout ? ExecutionOutcomeStatus.TimedOut : ExecutionOutcomeStatus.Failed,
                output: null,
                error: ex.Message,
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
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await _lifecycleManager.EnsureCleanupAsync(spec.AgentRunId, cleanupCts.Token);
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

        args.Add("-e");
        args.Add($"SESSION_API_KEY={sessionApiKey}");

        args.Add("-e");
        args.Add("OPENHANDS_SUPPRESS_BANNER=1");

        args.Add("-v");
        args.Add($"{workspacePath}:/workspace");

        args.Add(_options.DockerImage);

        if (!string.IsNullOrWhiteSpace(_options.ImportModules))
        {
            args.Add("--import-modules");
            args.Add(_options.ImportModules);
        }

        var processRequest = new ProcessRunRequest(
            "docker",
            args,
            TimeSpan.FromSeconds(30),
            WorkingDirectory: workspacePath);

        var result = await _processRunner.RunAsync(processRequest, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to start OpenHands container '{containerName}': {result.StandardError}");
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

    private async Task<(string ModelId, string ApiKey, string BaseUrl)> ResolveModelAndApiKeyAsync(
        AgentExecutionSpec spec,
        CancellationToken cancellationToken)
    {
        string modelId;
        string baseUrl = _options.ProviderBaseUrl;
        string? secretName = _options.ProviderApiKeySecretName;
        string? envVar = _options.ProviderApiKeyEnvironmentVariable;

        if (_gatewayResolver is not null)
        {
            var resolution = _gatewayResolver.Resolve(spec.Model);
            if (!resolution.IsResolved)
            {
                throw new InvalidOperationException(resolution.FailureReason ?? "Model gateway could not be resolved.");
            }

            var gateway = resolution.Gateway!;
            var transport = gateway.GetTransportInfo();
            baseUrl = transport.BaseUrl;
            secretName = transport.ApiKeySecretName ?? _options.ProviderApiKeySecretName;
            envVar = transport.EnvironmentVariableName ?? _options.ProviderApiKeyEnvironmentVariable;
            modelId = gateway.FormatModelIdentifier(spec.Model.ModelId ?? _options.DefaultModel);
        }
        else
        {
            modelId = !string.IsNullOrWhiteSpace(spec.Model.ModelId)
                ? spec.Model.ModelId
                : _options.DefaultModel;

            if (!modelId.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase))
            {
                modelId = $"openrouter/{modelId}";
            }
        }

        string? apiKey = null;

        if (_secretProvider is not null && !string.IsNullOrWhiteSpace(secretName))
        {
            apiKey = await _secretProvider.GetSecretAsync(secretName, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(envVar))
        {
            apiKey = Environment.GetEnvironmentVariable(envVar);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key could be resolved for OpenHands provider ({secretName}).");
        }

        return (modelId, apiKey, baseUrl);
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
                var prompt = tokens.PromptTokens ?? 0;
                var completion = tokens.CompletionTokens ?? 0;
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
