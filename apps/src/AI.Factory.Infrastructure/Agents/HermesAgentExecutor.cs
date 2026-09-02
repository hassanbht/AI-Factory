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

    public HermesAgentExecutor(
        HermesOptions options,
        IProcessRunner processRunner,
        ILogger<HermesAgentExecutor> logger,
        IAuditLogger? auditLogger = null)
    {
        _options = options;
        _processRunner = processRunner;
        _logger = logger;
        _auditLogger = auditLogger ?? NullAuditLogger.Instance;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // A fresh, isolated workspace per execution — never trust or reuse a
        // caller-supplied path for the sandboxed bind mount. Guarded again
        // explicitly (not just "it's a Guid, so it's fine by construction")
        // so a future change to WorkspaceRoot/this construction is caught
        // programmatically rather than relying on the code staying correct.
        string workspacePath;

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

        var arguments = new List<string>
        {
            "run",
            "--rm",
            "--network",
            _options.NetworkMode,
            "-v",
            $"{_options.DataVolume}:/opt/data",
            "-v",
            $"{workspacePath}:/workspace",
            _options.DockerImage,
            "-z",
            request.Prompt
        };

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
