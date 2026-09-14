using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
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

    public HermesAgentExecutor(
        HermesOptions options,
        IProcessRunner processRunner,
        ILogger<HermesAgentExecutor> logger,
        IAuditLogger? auditLogger = null,
        ISecretProvider? secretProvider = null)
    {
        _options = options;
        _processRunner = processRunner;
        _logger = logger;
        _auditLogger = auditLogger ?? NullAuditLogger.Instance;
        _secretProvider = secretProvider;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // Phase 18.14 — request.WorkingDirectory now carries the Factory-
        // resolved Project workspace (AgentOrchestrator -> IWorkspaceResolver
        // -> HermesAgentRuntime), when the AgentRun is WorkItem-backed. Only
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

        var arguments = new List<string>
        {
            "run",
            "--rm",
            "--network",
            _options.NetworkMode,
            "-v",
            $"{_options.DataVolume}:/opt/data",
            "-v",
            $"{workspacePath}:/workspace"
        };

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

        if (!string.IsNullOrWhiteSpace(_options.ProviderApiKeyEnvironmentVariable)
            && !string.IsNullOrWhiteSpace(_options.ProviderApiKeySecretName)
            && _secretProvider is not null)
        {
            var providerApiKey = await _secretProvider.GetSecretAsync(_options.ProviderApiKeySecretName, cancellationToken);

            if (!string.IsNullOrEmpty(providerApiKey))
            {
                arguments.Add("-e");
                arguments.Add($"{_options.ProviderApiKeyEnvironmentVariable}={providerApiKey}");
            }
        }

        arguments.Add(_options.DockerImage);
        arguments.Add("-z");
        arguments.Add(request.Prompt);

        // AgentRun/AgentExecutionRequest have always carried a Model — this
        // was the one place it silently never reached the Hermes CLI,
        // leaving every execution on Hermes's own persisted default model
        // with no way for a caller to override it.
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("-m");
            arguments.Add(request.Model);
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

        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    "docker",
                    arguments,
                    TimeSpan.FromSeconds(_options.TimeoutSeconds),
                    WorkingDirectory: workspacePath,
                    EnvironmentVariables: controlledEnvironment),
                cancellationToken);

            stopwatch.Stop();

            if (result.TimedOut)
            {
                _logger.LogWarning(
                    "Hermes execution timed out for agent {Agent} after {TimeoutSeconds}s",
                    request.Agent,
                    _options.TimeoutSeconds);

                return new AgentExecutionResult(
                    false,
                    Truncate(result.StandardOutput),
                    $"Execution timed out after {_options.TimeoutSeconds} seconds.",
                    stopwatch.ElapsedMilliseconds,
                    workspacePath);
            }

            var success = result.ExitCode == 0;

            _logger.LogInformation(
                "Hermes execution completed for agent {Agent} with exit code {ExitCode} in {DurationMs}ms",
                request.Agent,
                result.ExitCode,
                stopwatch.ElapsedMilliseconds);

            string? error = null;

            if (!success)
            {
                var truncatedError = Truncate(result.StandardError);
                error = string.IsNullOrEmpty(truncatedError)
                    ? $"Process exited with code {result.ExitCode}."
                    : truncatedError;
            }

            return new AgentExecutionResult(
                success,
                Truncate(result.StandardOutput),
                error,
                stopwatch.ElapsedMilliseconds,
                workspacePath);
        }
        catch (ProcessLaunchException ex)
        {
            stopwatch.Stop();

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
}
