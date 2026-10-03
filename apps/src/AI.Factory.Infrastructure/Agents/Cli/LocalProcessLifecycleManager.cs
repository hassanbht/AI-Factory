using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents.Cli;

/// <summary>
/// Phase M — the local-process counterpart of <see cref="DockerContainerLifecycleManager"/> for the CLI
/// adapters. A Worker that dies leaves its CLI child running (children are not killed with their parent on
/// Windows or Linux) and its isolated runtime home on disk. Every CLI run is identifiable by its directory
/// <c>{StateRoot}/{agentRunId:N}</c>, which holds the child's pid + start time:
/// <list type="bullet">
/// <item><see cref="EnsureCleanupAsync"/> kills that exact process tree (the start time guards against a
/// recycled pid) and deletes the directory — idempotent when the process already exited or never existed;</item>
/// <item><see cref="SweepOrphanContainersAsync"/> does the same for every directory whose AgentRun is unknown,
/// terminal, or has had no heartbeat for <see cref="RecoveryOptions.OrphanHeartbeatTimeout"/>. A run that is
/// still heartbeating (this Worker's or a peer's) is never touched.</item>
/// </list>
/// </summary>
public sealed class LocalProcessLifecycleManager : IContainerLifecycleManager
{
    private readonly CliRuntimeOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RecoveryOptions _recoveryOptions;
    private readonly ILogger<LocalProcessLifecycleManager> _logger;

    public LocalProcessLifecycleManager(
        CliRuntimeOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<LocalProcessLifecycleManager> logger,
        RecoveryOptions? recoveryOptions = null)
    {
        _options = options;
        _scopeFactory = scopeFactory;
        _recoveryOptions = recoveryOptions ?? new RecoveryOptions();
        _logger = logger;
    }

    public Task EnsureCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default)
    {
        Reap(CliRunSandbox.RunDirectoryFor(_options.StateRoot, agentRunId), agentRunId, "EnsureCleanup");
        return Task.CompletedTask;
    }

    public async Task<int> SweepOrphanContainersAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(_options.StateRoot);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var agentRuns = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
        var cutoffUtc = DateTime.UtcNow - _recoveryOptions.OrphanHeartbeatTimeout;
        var swept = 0;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var agentRunId))
            {
                continue; // Not a Factory run directory: never touched.
            }

            var run = await agentRuns.GetByIdAsync(agentRunId, cancellationToken);
            var reason = run is null ? "AgentRunNotFound"
                : AgentRunTransitions.IsTerminal(run.Status) ? $"AgentRunAlreadyTerminal Status={run.Status}"
                : run.UpdatedAtUtc < cutoffUtc ? $"AgentRunStale UpdatedAtUtc={run.UpdatedAtUtc:o}"
                : null;

            if (reason is null)
            {
                continue;
            }

            Reap(directory, agentRunId, reason);
            swept++;
        }

        return swept;
    }

    private void Reap(string runDirectory, Guid agentRunId, string reason)
    {
        if (!Directory.Exists(runDirectory))
        {
            return;
        }

        var killed = CliRunSandbox.KillRecordedProcess(runDirectory, _logger);
        CliRunSandbox.DeleteDirectory(runDirectory, _logger);

        _logger.LogInformation(
            "OrphanCliRuntimeReaped AgentRunId={AgentRunId} ProcessKilled={ProcessKilled} Reason={Reason}",
            agentRunId, killed, reason);
    }
}
