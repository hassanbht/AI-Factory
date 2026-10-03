using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// Phase D.1 — Docker Container Lifecycle Manager.
/// Provides deterministic cleanup of execution containers and safe, bounded startup orphan sweeps.
///
/// Principles:
///   1. Container identification via deterministic labels (factory=ai-factory, managed=true, agent-run-id, project-id).
///   2. Active cleanup on completion, error, timeout, cancellation, or startup failure.
///   3. Safe startup sweep: only removes Factory-owned containers whose AgentRun is missing, terminal, or stale.
///   4. Unmanaged / foreign containers are NEVER removed or killed.
/// </summary>
public sealed class DockerContainerLifecycleManager : IContainerLifecycleManager
{
    private readonly IProcessRunner _processRunner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RecoveryOptions _recoveryOptions;
    private readonly ILogger<DockerContainerLifecycleManager> _logger;

    public DockerContainerLifecycleManager(
        IProcessRunner processRunner,
        IServiceScopeFactory scopeFactory,
        ILogger<DockerContainerLifecycleManager> logger,
        RecoveryOptions? recoveryOptions = null)
    {
        _processRunner = processRunner;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _recoveryOptions = recoveryOptions ?? new RecoveryOptions();
    }

    public async Task EnsureCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default)
    {
        await DockerContainerCleanup.RemoveAsync(
            _processRunner,
            DockerContainerNaming.GetContainerName(agentRunId),
            _logger,
            reconcile: false,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Phase O — cleanup after an interrupted launch: keeps removing this run's container for a short bounded window,
    /// because the daemon may still create it after the launch client was killed. See <see cref="DockerContainerCleanup"/>.
    /// </summary>
    public async Task<ContainerCleanupOutcome> ReconcileCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default)
    {
        var result = await DockerContainerCleanup.RemoveAsync(
            _processRunner,
            DockerContainerNaming.GetContainerName(agentRunId),
            _logger,
            reconcile: true,
            cancellationToken: cancellationToken);

        return result.Outcome;
    }

    public async Task<int> SweepOrphanContainersAsync(CancellationToken cancellationToken = default)
    {
        var request = new ProcessRunRequest(
            "docker",
            new[]
            {
                "ps", "-a",
                "--filter", $"label={DockerContainerNaming.FactoryLabelKey}={DockerContainerNaming.FactoryLabelValue}",
                "--filter", $"label={DockerContainerNaming.ManagedLabelKey}={DockerContainerNaming.ManagedLabelValue}",
                "--format", "{{.ID}}\t{{.Names}}\t{{.Labels}}\t{{.Status}}"
            },
            TimeSpan.FromSeconds(30));

        ProcessRunResult result;
        try
        {
            result = await _processRunner.RunAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query docker containers for orphan sweep.");
            return 0;
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return 0;
        }

        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sweptCount = 0;

        using var scope = _scopeFactory.CreateScope();
        var agentRunRepository = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
        var cutoffUtc = DateTime.UtcNow - _recoveryOptions.StaleExecutionTimeout;

        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var containerId = parts[0].Trim();
            var containerName = parts[1].Trim();
            var labelsText = parts[2].Trim();
            var status = parts.Length >= 4 ? parts[3].Trim() : string.Empty;

            if (!containerName.StartsWith(DockerContainerNaming.ContainerNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var labels = ParseLabels(labelsText);

            if (!labels.TryGetValue(DockerContainerNaming.FactoryLabelKey, out var factory)
                || !string.Equals(factory, DockerContainerNaming.FactoryLabelValue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!labels.TryGetValue(DockerContainerNaming.ManagedLabelKey, out var managed)
                || !string.Equals(managed, DockerContainerNaming.ManagedLabelValue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!labels.TryGetValue(DockerContainerNaming.AgentRunIdLabelKey, out var runIdStr)
                || !Guid.TryParse(runIdStr, out var agentRunId))
            {
                _logger.LogWarning(
                    "Found Factory-labeled container {ContainerId} ({ContainerName}) with invalid or missing agent-run-id. Preserving container without deleting.",
                    containerId, containerName);
                continue;
            }

            Guid? projectId = null;
            if (labels.TryGetValue(DockerContainerNaming.ProjectIdLabelKey, out var projIdStr)
                && Guid.TryParse(projIdStr, out var parsedProjId))
            {
                projectId = parsedProjId;
            }

            var agentRun = await agentRunRepository.GetByIdAsync(agentRunId, cancellationToken);

            bool shouldRemove = false;
            string reason;

            if (agentRun is null)
            {
                shouldRemove = true;
                reason = "AgentRunNotFound";
            }
            else if (AgentRunTransitions.IsTerminal(agentRun.Status))
            {
                shouldRemove = true;
                reason = $"AgentRunAlreadyTerminal Status={agentRun.Status}";
            }
            else if (agentRun.UpdatedAtUtc < cutoffUtc)
            {
                shouldRemove = true;
                reason = $"AgentRunStale UpdatedAtUtc={agentRun.UpdatedAtUtc:o}";
            }
            else
            {
                // Active running run heartbeating within StaleExecutionTimeout
                _logger.LogDebug(
                    "Container {ContainerId} ({ContainerName}) is active for running AgentRun {AgentRunId}. Preserving.",
                    containerId, containerName, agentRunId);
                continue;
            }

            if (shouldRemove)
            {
                _logger.LogInformation(
                    "OrphanContainerRemoved ContainerId={ContainerId} ContainerName={ContainerName} Reason={Reason} AgentRunId={AgentRunId} ProjectId={ProjectId}",
                    containerId, containerName, reason, agentRunId, projectId);

                try
                {
                    using var rmCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _processRunner.RunAsync(
                        new ProcessRunRequest("docker", new[] { "rm", "-f", "-v", containerId }, TimeSpan.FromSeconds(10)),
                        rmCts.Token);

                    sweptCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remove orphan container {ContainerId} ({ContainerName}).", containerId, containerName);
                }
            }
        }

        return sweptCount;
    }

    private static Dictionary<string, string> ParseLabels(string labelsText)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = labelsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            var eq = entry.IndexOf('=');
            if (eq > 0)
            {
                var k = entry[..eq].Trim();
                var v = entry[(eq + 1)..].Trim();
                dict[k] = v;
            }
        }
        return dict;
    }
}
