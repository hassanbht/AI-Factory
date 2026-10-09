using System.Diagnostics;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Security;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents;

public sealed class HermesAgentExecutor : IAgentExecutor
{
    private readonly HermesOptions _options;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<HermesAgentExecutor> _logger;
    private readonly IAuditLogger _auditLogger;
    private readonly ISecretProvider? _secretProvider;
    private readonly ModelGatewayResolver? _gatewayResolver;
    private readonly AI.Factory.Application.Abstractions.Execution.IProviderCaptureCoordinator? _capture;

    public HermesAgentExecutor(
        HermesOptions options,
        IProcessRunner processRunner,
        ILogger<HermesAgentExecutor> logger,
        IAuditLogger? auditLogger = null,
        ISecretProvider? secretProvider = null,
        ModelGatewayResolver? gatewayResolver = null,
        AI.Factory.Application.Abstractions.Execution.IProviderCaptureCoordinator? capture = null)
    {
        _options = options;
        _processRunner = processRunner;
        _logger = logger;
        _auditLogger = auditLogger ?? NullAuditLogger.Instance;
        _secretProvider = secretProvider;
        _gatewayResolver = gatewayResolver;
        _capture = capture;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!string.IsNullOrWhiteSpace(request.Model) && AI.Factory.Application.Orchestration.ModelRoutingOptions.IsAutoOrImplicitModel(request.Model))
        {
            var message = $"Hermes execution refused: model '{request.Model}' is an automatic or implicit identifier.";
            _logger.LogWarning(message);
            return new AgentExecutionResult(false, null, message, stopwatch.ElapsedMilliseconds);
        }

        var gatewayRejection = CheckGatewayRoute(request.Gateway);
        if (gatewayRejection is not null)
        {
            _logger.LogWarning(
                "Hermes execution refused for agent {Agent}: {Reason}", request.Agent, gatewayRejection);
            return new AgentExecutionResult(false, null, gatewayRejection, stopwatch.ElapsedMilliseconds);
        }

        // Phase Q.3 design blocker: Hermes runs inside Docker, but there is no
        // verified host-loopback route from its container network namespace. The
        // reverse proxy/coordinator is deliberately NOT activated or injected here;
        // a captured response from a separate request would misattribute cost.
        // Until a supported in-container transport interception is verified, cost
        // remains UNKNOWN and the optional coordinator seam is compatibility-only.

        // Phase 18.14 — request.WorkingDirectory now carries the Factory-
        // resolved Project workspace (AgentOrchestrator -> IWorkspaceResolver
        // -> HermesExecutionAdapter), when the AgentRun is WorkItem-backed. Only
        // when it is empty (the one remaining legitimate case: a
        // Planner-only AgentRun with no WorkItemId) do we fall back to the
        // original fresh, isolated scratch folder — never trust or reuse a
        // caller-supplied path for THAT sandboxed bind mount, since nothing
        // upstream resolved/validated it in that case.
        string workspacePath;

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            workspacePath = request.WorkingDirectory;

            // The resolver already validated this directory exists at
            // resolution time, but it could have vanished (or this could be
            // a stale in-flight execution surviving a Worker restart)
            // between resolution and this call — never silently fall back to
            // scratch, that would defeat the whole point of a Factory-owned
            // workspace.
            if (!Directory.Exists(workspacePath))
            {
                var message = $"Resolved workspace '{workspacePath}' no longer exists or is not accessible.";

                await _auditLogger.LogAsync(
                    Guid.NewGuid(), AuditEventType.WorkflowFailed, AuditSeverity.Critical,
                    $"Security violation: {message}", cancellationToken: cancellationToken);

                _logger.LogError(
                    "Resolved workspace missing for agent {Agent}: {WorkspacePath}", request.Agent, workspacePath);

                return new AgentExecutionResult(false, null, message, 0, WorkspacePath: null);
            }
        }
        else
        {
            // A fresh, isolated workspace per execution — never trust or reuse
            // a caller-supplied path for the sandboxed bind mount. Guarded
            // again explicitly (not just "it's a Guid, so it's fine by
            // construction") so a future change to WorkspaceRoot/this
            // construction is caught programmatically rather than relying on
            // the code staying correct.
            try
            {
                workspacePath = WorkspacePathGuard.EnsureWithinRoot(
                    _options.WorkspaceRoot, Guid.NewGuid().ToString("N"));
            }
            catch (SecurityViolationException ex)
            {
                await _auditLogger.LogAsync(
                    Guid.NewGuid(), AuditEventType.WorkflowFailed, AuditSeverity.Critical,
                    $"Security violation: {ex.Message}", cancellationToken: cancellationToken);

                _logger.LogError(ex, "Workspace path security violation for agent {Agent}", request.Agent);

                return new AgentExecutionResult(false, null, ex.Message, 0);
            }

            Directory.CreateDirectory(workspacePath);
        }

        var agentRunId = request.AgentRunId ?? Guid.NewGuid();
        var containerName = DockerContainerNaming.GetContainerName(agentRunId);

        var arguments = new List<string>
        {
            "run",
            "--rm",
            "--name",
            containerName,
            "--label",
            $"{DockerContainerNaming.FactoryLabelKey}={DockerContainerNaming.FactoryLabelValue}",
            "--label",
            $"{DockerContainerNaming.AgentRunIdLabelKey}={agentRunId}",
            "--label",
            $"{DockerContainerNaming.AdapterLabelKey}={request.Adapter ?? "Hermes"}",
            "--label",
            $"{DockerContainerNaming.ManagedLabelKey}={DockerContainerNaming.ManagedLabelValue}"
        };

        if (request.ProjectId.HasValue)
        {
            arguments.Add("--label");
            arguments.Add($"{DockerContainerNaming.ProjectIdLabelKey}={request.ProjectId.Value}");
        }

        // A host-local listener is not reachable as 127.0.0.1 from a bridged container.
        // The capture proxy is therefore deliberately not injected into this Docker route;
        // the real Hermes/OpenRouter transport path has no supported, verified interception.
        arguments.Add("--network");
        arguments.Add(_options.NetworkMode);

        // Phase M — credential isolation. /opt/data is HERMES_HOME, and Hermes loads
        // $HERMES_HOME/.env with override=True (hermes_cli/env_loader.py, 0.20.6) — so whatever key a
        // mounted home holds beats the one the Factory injects below. The Factory used to mount the
        // developer's ~/.hermes there by default: a run silently billed another account (benchmark
        // factory-T1-oldkey/T2-oldkey). An anonymous volume is created empty from the image for this
        // container alone and removed with it (--rm / rm -v), so no ambient Hermes configuration,
        // session or credential can reach a run, and nothing a run writes there survives it.
        arguments.Add("-v");
        arguments.Add("/opt/data");

        // Phase 18.29.1 — how /workspace is mounted depends on WHOSE daemon runs
        // the container, not on what the agent does. See BuildWorkspaceMountArguments.
        try
        {
            arguments.AddRange(BuildWorkspaceMountArguments(workspacePath));
        }
        catch (SecurityViolationException ex)
        {
            await _auditLogger.LogAsync(
                Guid.NewGuid(), AuditEventType.WorkflowFailed, AuditSeverity.Critical,
                $"Security violation: {ex.Message}", cancellationToken: cancellationToken);

            _logger.LogError(ex, "Workspace mount security violation for agent {Agent}", request.Agent);

            return new AgentExecutionResult(false, null, ex.Message, 0, WorkspacePath: null);
        }

        // `-e NAME=VALUE` are docker-run flags that inject a variable INTO
        // the container — distinct from (and never mixed up with) the host
        // docker.exe process's own environment set below via
        // ProcessRunRequest.EnvironmentVariables. Non-secret values (e.g. a
        // provider base-url override) come straight from config; the
        // provider API key, if configured, is resolved through
        // ISecretProvider so it never sits in appsettings/config in plain
        // text and is never logged.
        foreach (var (key, value) in _options.AdditionalEnvironmentVariables)
        {
            arguments.Add("-e");
            arguments.Add($"{key}={value}");
        }

        // Phase 18.24 — Hermes tool safety restricts write_file/edit_file to
        // HERMES_WRITE_SAFE_ROOT (defaulting to /opt/data). Without /workspace
        // in that root, any agent write tool targeting the mounted workspace
        // is rejected by Hermes. Default to /workspace if not overridden.
        if (!_options.AdditionalEnvironmentVariables.ContainsKey("HERMES_WRITE_SAFE_ROOT"))
        {
            arguments.Add("-e");
            arguments.Add("HERMES_WRITE_SAFE_ROOT=/workspace");
        }

        // Phase K — the key VALUE never goes on the docker command line, where every local user
        // can read it for the whole run (`ps`, /proc/<pid>/cmdline, Win32_Process.CommandLine).
        // `-e NAME` makes docker copy the value from its own process environment, which is the
        // controlled allow-list below.
        string? providerApiKey = null;

        if (!string.IsNullOrWhiteSpace(_options.ProviderApiKeyEnvironmentVariable)
            && !string.IsNullOrWhiteSpace(_options.ProviderApiKeySecretName)
            && _secretProvider is not null)
        {
            providerApiKey = await _secretProvider.GetSecretAsync(_options.ProviderApiKeySecretName, cancellationToken);

            if (string.IsNullOrEmpty(providerApiKey))
            {
                // Phase M — the Factory is the credential authority: a configured credential that
                // cannot be resolved stops the run instead of launching it without one.
                var message = $"Factory credential '{_options.ProviderApiKeySecretName}' could not be resolved; "
                    + "Hermes was not started (no ambient credential is ever used in its place).";
                _logger.LogError("Hermes execution refused for agent {Agent}: {Reason}", request.Agent, message);
                return new AgentExecutionResult(false, null, message, stopwatch.ElapsedMilliseconds, workspacePath);
            }

            arguments.Add("-e");
            arguments.Add(_options.ProviderApiKeyEnvironmentVariable);
        }

        arguments.Add(_options.DockerImage);
        arguments.Add("-z");
        arguments.Add(request.Prompt);

        // Phase J — Hermes' own machine-readable run report. `hermes -z` exits 0 even when
        // the model gateway rejected every call (verified live: OpenRouter HTTP 402 printed
        // as the "final response", exit code 0), so the exit code alone cannot tell a model
        // generation from a gateway failure. The report's "failed" flag can. Written into
        // the mounted workspace, the one location both mount topologies share with the
        // Worker; read and deleted by ReadRunReport before anything inspects the workspace.
        arguments.Add("--usage-file");
        arguments.Add($"/workspace/{RunReportFileName(agentRunId)}");

        // AgentRun/AgentExecutionRequest have always carried a Model — this
        // was the one place it silently never reached the Hermes CLI,
        // leaving every execution on Hermes's own persisted default model
        // with no way for a caller to override it. Phase 18.20: the caller's
        // own Model still wins, but a configured HermesOptions.DefaultModel
        // now covers every run that has none — which is every run the
        // scheduler and planner create.
        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.DefaultModel : request.Model;

        if (!string.IsNullOrWhiteSpace(model) && AI.Factory.Application.Orchestration.ModelRoutingOptions.IsAutoOrImplicitModel(model))
        {
            var message = $"Hermes execution refused: model '{model}' is an automatic or implicit identifier.";
            _logger.LogWarning(message);
            return new AgentExecutionResult(false, null, message, stopwatch.ElapsedMilliseconds, workspacePath);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("-m");
            arguments.Add(model);
        }

        if (!string.IsNullOrWhiteSpace(_options.Provider))
        {
            arguments.Add("--provider");
            arguments.Add(_options.Provider);
        }

        _logger.LogInformation(
            "Hermes execution started for agent {Agent} in workspace {WorkspacePath}",
            request.Agent,
            workspacePath);

        // Explicit allow-list, not full inheritance of this process's
        // environment — the container never needs, and must never see,
        // this API/Worker process's own secrets (DB connection strings,
        // etc). PATH is the only thing docker itself needs to resolve.
        var controlledEnvironment = new Dictionary<string, string>();
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrEmpty(inheritedPath))
        {
            controlledEnvironment["PATH"] = inheritedPath;
        }

        if (!string.IsNullOrEmpty(providerApiKey))
        {
            controlledEnvironment[_options.ProviderApiKeyEnvironmentVariable!] = providerApiKey;
        }

        TimeSpan processTimeout;
        if (request.Deadline.HasValue)
        {
            var remaining = request.Deadline.Value - DateTimeOffset.UtcNow;
            processTimeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
            if (_options.TimeoutSeconds > 0 && TimeSpan.FromSeconds(_options.TimeoutSeconds) < processTimeout)
            {
                processTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
            }
        }
        else if (request.Timeout.HasValue)
        {
            processTimeout = request.Timeout.Value;
            if (_options.TimeoutSeconds > 0 && TimeSpan.FromSeconds(_options.TimeoutSeconds) < processTimeout)
            {
                processTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
            }
        }
        else
        {
            processTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        }

        // Phase O — the `docker run` client was started and did not come back normally (cancelled, timed out, failed
        // while running): the daemon may still be creating the container, so cleanup reconciles instead of looking once.
        var launched = false;
        var finishedNormally = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Phase L — only a report Hermes writes during THIS launch may count: a stale or pre-placed
            // file at that path (the workspace is agent-writable) must not stand in for one.
            DeleteRunReport(workspacePath, agentRunId);

            var startedAt = DateTimeOffset.UtcNow;
            launched = true;
            var result = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    "docker",
                    arguments,
                    processTimeout,
                    WorkingDirectory: workspacePath,
                    EnvironmentVariables: controlledEnvironment),
                cancellationToken);

            stopwatch.Stop();
            finishedNormally = !result.TimedOut;

            if (result.TimedOut)
            {
                _logger.LogWarning(
                    "Hermes execution timed out for agent {Agent} after {TimeoutSeconds:F0}s",
                    request.Agent,
                    processTimeout.TotalSeconds);

                return new AgentExecutionResult(
                    false,
                    Truncate(result.StandardOutput),
                    $"Execution timed out after {processTimeout.TotalSeconds:F0} seconds.",
                    stopwatch.ElapsedMilliseconds,
                    workspacePath,
                    TimedOut: true);
            }

            // Phase L — exit 0 is not evidence of success on its own. Hermes 0.20.6 writes the
            // report on every terminal path of `-z` (oneshot.py `_write_usage_file`), so a
            // successful run is exit 0 AND a report saying "failed": false, "completed": true.
            // A missing/unreadable report or an incomplete run (iteration budget exhausted,
            // stopped on invalid tool calls — Hermes still exits 0 when it printed something)
            // is an execution-integrity failure, not a success.
            var report = ReadRunReport(workspacePath, agentRunId);
            var runtimeReportedFailure = result.ExitCode == 0 && report?.Failed == true;
            var reportUnavailable = result.ExitCode == 0 && report is null;
            var runIncomplete = result.ExitCode == 0 && report is { Failed: false, Completed: false };
            var success = result.ExitCode == 0 && report is { Failed: false, Completed: true };

            _logger.LogInformation(
                "Hermes execution completed for agent {Agent} with exit code {ExitCode} in {DurationMs}ms",
                request.Agent,
                result.ExitCode,
                stopwatch.ElapsedMilliseconds);

            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var preview = result.StandardOutput.Length <= 1000
                    ? result.StandardOutput
                    : result.StandardOutput[^1000..];
                _logger.LogInformation(
                    "Hermes agent {Agent} output tail: {Output}", request.Agent, SensitiveDataSanitizer.Sanitize(preview));
            }

            string? error = null;

            if (runtimeReportedFailure)
            {
                // Hermes prints its failure summary (e.g. "Billing or credits exhausted:
                // HTTP 402: ...") as the final response on stdout.
                _logger.LogWarning(
                    "Hermes reported a failed run with exit code 0 for agent {Agent}; treating it as failed.",
                    request.Agent);

                error = RuntimeReportedFailurePrefix
                    + ModelGatewayFailureClassifier.SanitizeUpstreamMessage(result.StandardOutput, keepTail: true);
            }
            else if (reportUnavailable)
            {
                error = ReportUnavailableError;
            }
            else if (runIncomplete)
            {
                error = RunIncompleteError;
            }
            else if (!success)
            {
                var providerError = ExtractProviderError(result.StandardOutput, result.StandardError);
                if (providerError is not null)
                {
                    error = $"Hermes provider error (exit code {result.ExitCode}): {providerError}";
                }
                else
                {
                    var truncatedError = Truncate(result.StandardError);
                    error = string.IsNullOrEmpty(truncatedError)
                        ? $"Process exited with code {result.ExitCode}."
                        : truncatedError;
                }
            }

            // On an exit-0 failure stdout is a failure summary or unproven output, not agent
            // output, so it is persisted under the same redaction as the error itself.
            return new AgentExecutionResult(
                success,
                result.ExitCode == 0 && !success && result.StandardOutput is not null
                    ? Truncate(SensitiveDataSanitizer.Sanitize(result.StandardOutput))
                    : Truncate(result.StandardOutput),
                error,
                stopwatch.ElapsedMilliseconds,
                workspacePath,
                ExitCode: result.ExitCode,
                Usage: report?.Usage,
                Report: new ExecutionRuntimeReport(
                    Runtime: request.Adapter ?? "Hermes",
                    RuntimeVersion: _options.DockerImage,
                    SessionId: report?.SessionId,
                    StartedAt: startedAt,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ExitCode: result.ExitCode,
                    CostUsd: report?.CostUsd,
                    CostSource: report?.CostSource,
                    Turns: report?.ApiCalls,
                    ReportedModel: report?.Model));
        }
        catch (ProcessLaunchException ex)
        {
            stopwatch.Stop();
            launched = false; // docker.exe itself never started: no client, so no daemon request in flight.

            _logger.LogError(
                ex,
                "Failed to launch Hermes process for agent {Agent}",
                request.Agent);

            return new AgentExecutionResult(
                false,
                null,
                $"Hermes could not be started: {ex.Message}",
                stopwatch.ElapsedMilliseconds,
                workspacePath);
        }
        catch (SecurityViolationException ex)
        {
            stopwatch.Stop();

            await _auditLogger.LogAsync(
                Guid.NewGuid(), AuditEventType.WorkflowFailed, AuditSeverity.Critical,
                $"Security violation: {ex.Message}", cancellationToken: cancellationToken);

            _logger.LogError(ex, "Command policy violation for agent {Agent}", request.Agent);

            return new AgentExecutionResult(
                false, null, $"Security violation: {ex.Message}", stopwatch.ElapsedMilliseconds, workspacePath);
        }
        finally
        {
            await CleanupContainerAsync(containerName, reconcile: launched && !finishedNormally);

            // Timeout, cancellation and launch failures never reach ReadRunReport; the report
            // must not be left in a workspace that is a git working tree.
            DeleteRunReport(workspacePath, agentRunId);
        }
    }

    /// <summary>
    /// Phase J/K — Hermes reaches a model through exactly one provider, <see cref="HermesOptions.Provider"/>
    /// (`--provider`), with one configured API key and its built-in endpoint for that provider. The
    /// routed gateway is the authority for which gateway that must be: a call with no route (a
    /// profile without a Gateway) is checked against the default gateway; a route to a
    /// gateway Hermes is not configured for — another provider, another credential, or an endpoint
    /// override pointing elsewhere — is refused rather than silently executed on the configured one,
    /// and an unknown/disabled gateway fails closed. Null when the route is acceptable.
    /// </summary>
    private string? CheckGatewayRoute(string? gatewayKey)
    {
        if (_gatewayResolver is null)
        {
            return null;
        }

        var resolution = string.IsNullOrWhiteSpace(gatewayKey)
            ? _gatewayResolver.Resolve((ModelRoute?)null)
            : _gatewayResolver.Resolve(gatewayKey);
        if (!resolution.IsResolved || resolution.Gateway is null)
        {
            return $"Model gateway route rejected: {resolution.FailureReason}";
        }

        var transport = resolution.Gateway.GetTransportInfo();
        var reject = $"Model gateway route rejected: Hermes cannot honour the routed gateway '{resolution.GatewayKey}': ";

        // Without an explicit --provider Hermes runs on whatever provider its data volume persisted,
        // which the Factory cannot see — that is a silent bypass of the route, not an unverified one.
        if (string.IsNullOrWhiteSpace(_options.Provider) || string.IsNullOrWhiteSpace(transport.CliProviderName))
        {
            return reject + $"Hermes:Provider must be set explicitly (the gateway needs '{transport.CliProviderName}').";
        }

        if (!string.Equals(_options.Provider, transport.CliProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return reject + $"it is configured for provider '{_options.Provider}', the gateway needs '{transport.CliProviderName}'.";
        }

        if (!string.IsNullOrWhiteSpace(_options.ProviderApiKeySecretName)
            && !string.Equals(_options.ProviderApiKeySecretName, transport.ApiKeySecretName, StringComparison.Ordinal))
        {
            return reject + $"its credential secret '{_options.ProviderApiKeySecretName}' is not the gateway's '{transport.ApiKeySecretName}'.";
        }

        if (!string.IsNullOrWhiteSpace(_options.ProviderApiKeyEnvironmentVariable)
            && !string.IsNullOrWhiteSpace(transport.EnvironmentVariableName)
            && !string.Equals(_options.ProviderApiKeyEnvironmentVariable, transport.EnvironmentVariableName, StringComparison.Ordinal))
        {
            return reject + $"it injects the key as '{_options.ProviderApiKeyEnvironmentVariable}', the provider reads '{transport.EnvironmentVariableName}'.";
        }

        // The endpoint Hermes will actually call: its {PROVIDER}_BASE_URL override, else its built-in
        // endpoint for that provider — known here only where verified; otherwise an override is required.
        var baseUrlVariable = $"{transport.CliProviderName.ToUpperInvariant()}_BASE_URL";
        var hermesEndpoint = _options.AdditionalEnvironmentVariables.TryGetValue(baseUrlVariable, out var overrideUrl)
            ? overrideUrl
            : HermesBuiltInEndpoints.GetValueOrDefault(transport.CliProviderName);

        if (hermesEndpoint is null)
        {
            return reject + $"Hermes has no verified built-in endpoint for provider '{transport.CliProviderName}'; "
                + $"set {baseUrlVariable} to the gateway's '{transport.BaseUrl}'.";
        }

        if (!string.Equals(hermesEndpoint.TrimEnd('/'), transport.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return reject + $"Hermes would call '{hermesEndpoint}' ({baseUrlVariable}), the gateway is '{transport.BaseUrl}'.";
        }

        return null;
    }

    /// <summary>
    /// Hermes' built-in endpoint per CLI provider when no {PROVIDER}_BASE_URL is set. Only entries
    /// verified live: `--provider openrouter` with no override reached https://openrouter.ai/api/v1
    /// (Phase J/K runs returned OpenRouter's own HTTP 402 body).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> HermesBuiltInEndpoints =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["openrouter"] = "https://openrouter.ai/api/v1"
        };

    /// <summary>Name of the per-run Hermes report inside the workspace (see <c>--usage-file</c>).</summary>
    public static string RunReportFileName(Guid agentRunId) => $".aif-hermes-run-{agentRunId:N}.json";

    /// <summary>Phase J — exit 0 with a report saying "failed": true; stdout is Hermes' failure summary.</summary>
    public const string RuntimeReportedFailurePrefix = "Hermes reported a failed run (exit code 0): ";

    /// <summary>Phase L — exit 0 with no readable run report: success cannot be proven.</summary>
    public const string ReportUnavailableError =
        "Hermes execution report unavailable (exit code 0): the run report was missing or unreadable, so success cannot be proven.";

    /// <summary>Phase L — exit 0, report "failed": false but "completed" not true.</summary>
    public const string RunIncompleteError =
        "Hermes reported an incomplete run (exit code 0): the conversation did not complete.";

    private sealed record HermesRunReport(
        bool Failed, bool Completed, ExecutionUsage? Usage, string? SessionId, decimal? CostUsd, string? CostSource,
        int? ApiCalls, string? Model);

    /// <summary>Hermes' report is a few hundred bytes; anything far larger is not its report.</summary>
    private const long MaxRunReportBytes = 64 * 1024;

    /// <summary>
    /// Reads and removes the report Hermes wrote for this run. Absent, unreadable or
    /// non-object report: null, which the caller treats as unproven (never as success).
    /// Token counts are kept only when positive — a zero from a run that never reached the
    /// model is "not measured", not a measurement.
    /// </summary>
    private HermesRunReport? ReadRunReport(string workspacePath, Guid agentRunId)
    {
        var path = Path.Combine(workspacePath, RunReportFileName(agentRunId));

        if (!File.Exists(path))
        {
            _logger.LogWarning("Hermes run report was not produced for AgentRun {AgentRunId}.", agentRunId);
            return null;
        }

        try
        {
            // The workspace is agent-writable: never follow a link or read an oversized file.
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || info.Length > MaxRunReportBytes)
            {
                _logger.LogWarning("Hermes run report for AgentRun {AgentRunId} was rejected (link or oversized).", agentRunId);
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("Hermes run report for AgentRun {AgentRunId} is not a JSON object.", agentRunId);
                return null;
            }

            var failed = root.TryGetProperty("failed", out var failedElement)
                && failedElement.ValueKind == JsonValueKind.True;
            var completed = root.TryGetProperty("completed", out var completedElement)
                && completedElement.ValueKind == JsonValueKind.True;

            var input = PositiveInt(root, "input_tokens");
            var output = PositiveInt(root, "output_tokens");
            var total = PositiveInt(root, "total_tokens");
            var usage = input is null && output is null && total is null
                ? null
                : new ExecutionUsage(input, output, total);

            // Phase M — Hermes' own estimate, recorded with its source/status, never computed here.
            decimal? cost = root.TryGetProperty("estimated_cost_usd", out var costElement)
                && costElement.ValueKind == JsonValueKind.Number && costElement.TryGetDecimal(out var c) && c >= 0
                    ? c
                    : null;
            var costStatus = Text(root, "cost_status");

            return new HermesRunReport(
                failed, completed, usage, Text(root, "session_id"), cost,
                cost is null ? null : $"hermes:estimated_cost_usd({costStatus ?? "unspecified"})",
                PositiveInt(root, "api_calls"), Text(root, "model"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Hermes run report for AgentRun {AgentRunId} could not be read.", agentRunId);
            return null;
        }
        finally
        {
            DeleteRunReport(workspacePath, agentRunId);
        }
    }

    private void DeleteRunReport(string workspacePath, Guid agentRunId)
    {
        try
        {
            File.Delete(Path.Combine(workspacePath, RunReportFileName(agentRunId)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Hermes run report for AgentRun {AgentRunId} could not be removed.", agentRunId);
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()
            : null;

    private static int? PositiveInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
            && value > 0
            ? value
            : null;

    private async Task CleanupContainerAsync(string containerName, bool reconcile)
    {
        // Never throws: it runs in a finally block and must not replace the run's own outcome.
        try
        {
            await DockerContainerCleanup.RemoveAsync(_processRunner, containerName, _logger, reconcile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hermes container cleanup for {ContainerName} threw.", containerName);
        }
    }

    /// <summary>
    /// Phase 18.29.1 — produces the `docker run` arguments that put the
    /// Factory-resolved workspace at /workspace inside the Hermes container.
    ///
    /// Two topologies, one execution contract:
    ///
    /// * Worker on the host (dev, tests, host deployment) — the daemon and the
    ///   Worker share a filesystem, so the original `-v &lt;path&gt;:/workspace`
    ///   bind mount is correct and is kept byte-for-byte.
    /// * Worker in a container talking to the host daemon over a mounted socket
    ///   (the production compose topology) — a bind-mount path is interpreted by
    ///   the HOST, where the Worker's `/var/lib/ai-factory/workspace/&lt;id&gt;` does
    ///   not exist. Docker would silently create an empty host directory and the
    ///   agent would run against nothing. The workspace is therefore addressed as
    ///   a subpath of the named volume both containers share.
    ///
    /// The subpath is always derived from the already-resolved workspace relative
    /// to <see cref="HermesOptions.WorkspaceRoot"/>, re-checked through
    /// <see cref="WorkspacePathGuard"/>. A workspace outside that root is refused
    /// rather than mounted — this must never become a way to hand Hermes a
    /// directory the workspace boundary would otherwise have rejected.
    /// </summary>
    private IReadOnlyList<string> BuildWorkspaceMountArguments(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(_options.WorkspaceVolumeName))
        {
            return new[] { "-v", $"{workspacePath}:/workspace" };
        }

        // Throws SecurityViolationException when the workspace escapes the root.
        var boundedWorkspace = WorkspacePathGuard.EnsureWithinRoot(_options.WorkspaceRoot, workspacePath);
        var resolvedRoot = WorkspacePathGuard.EnsureWithinRoot(_options.WorkspaceRoot, _options.WorkspaceRoot);

        var relative = Path.GetRelativePath(resolvedRoot, boundedWorkspace).Replace('\\', '/');

        // The root itself is a legitimate (if unusual) workspace — mount the whole
        // volume rather than emitting an empty/"." subpath the daemon would reject.
        return relative is "." or ""
            ? new[] { "--mount", $"type=volume,source={_options.WorkspaceVolumeName},target=/workspace" }
            : new[]
            {
                "--mount",
                $"type=volume,source={_options.WorkspaceVolumeName},target=/workspace,volume-subpath={relative}"
            };
    }

    private string? Truncate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return text.Length > _options.MaxOutputLength
            ? text[.._options.MaxOutputLength] + "...[truncated]"
            : text;
    }

    public static string? ExtractProviderError(string? stdout, string? stderr)
    {
        foreach (var text in new[] { stdout, stderr })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var reason = ModelGatewayFailureClassifier.FromRuntimeMessage(text);
            if (reason is not null)
            {
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var matchingLine = lines.LastOrDefault(l => l.Contains("HTTP 4") || l.Contains("HTTP 5"));
                if (!string.IsNullOrWhiteSpace(matchingLine))
                {
                    return ModelGatewayFailureClassifier.SanitizeUpstreamMessage(matchingLine);
                }

                return ModelGatewayFailureClassifier.SanitizeUpstreamMessage(text, keepTail: true);
            }
        }

        return null;
    }
}
