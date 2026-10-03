using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase M — deterministic recovery of AgentRuns whose Worker died mid-execution (benchmark
/// factory-T3-r1: the orphaned run stayed Running and its container kept working the same
/// workspace for ~15 minutes next to the replacement run).
///
/// Per orphan, strictly in this order:
/// <list type="number">
/// <item>mark it Failed (Infrastructure, orphan marker) under its concurrency token, re-checking that it
/// is still Running without a heartbeat — exactly one of several overlapping Workers wins, and a run that
/// heartbeated in the meantime is left untouched (runtime included);</item>
/// <item>the winner terminates its runtime resources (container / process tree) through every registered
/// <see cref="IContainerLifecycleManager"/> — idempotent, so a runtime that already exited or no longer
/// exists is a no-op;</item>
/// <item>only then hands the terminal run to <see cref="IAgentRunWorkItemSync"/>, which applies the
/// WorkItem's bounded retry budget. Nothing is re-executed here.</item>
/// </list>
/// Step 2 before step 3 is what guarantees the retry can never run concurrently with the old runtime.
/// Each candidate is isolated: one failure is logged and the others are still recovered.
/// A final sweep removes Factory-labelled runtimes whose run is terminal or unknown.
/// </summary>
public sealed class AgentRunOrphanRecovery
{
    private readonly IAgentRunRepository _agentRuns;
    private readonly IAgentRunWorkItemSync _workItemSync;
    private readonly IEnumerable<IContainerLifecycleManager> _runtimes;
    private readonly RecoveryOptions _options;
    private readonly ILogger<AgentRunOrphanRecovery> _logger;
    private readonly TimeProvider _time;

    public AgentRunOrphanRecovery(
        IAgentRunRepository agentRuns,
        IAgentRunWorkItemSync workItemSync,
        IEnumerable<IContainerLifecycleManager> runtimes,
        RecoveryOptions options,
        ILogger<AgentRunOrphanRecovery> logger,
        TimeProvider? time = null)
    {
        _agentRuns = agentRuns;
        _workItemSync = workItemSync;
        _runtimes = runtimes;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <returns>The number of AgentRuns this call recovered.</returns>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var cutoffUtc = _time.GetUtcNow().UtcDateTime - _options.OrphanHeartbeatTimeout;
        var candidates = await _agentRuns.GetRunningWithoutHeartbeatSinceAsync(cutoffUtc, cancellationToken);
        var recovered = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // 1. Claim it: only a run still Running without a heartbeat is marked, and only by one Worker.
                //    A run that heartbeated since it was listed is left alone — its runtime is never touched.
                var run = await _agentRuns.TryRecoverOrphanedRunAsync(
                    candidate.Id,
                    cutoffUtc,
                    $"no heartbeat for {_options.OrphanHeartbeatTimeout} (Worker presumed dead); runtime resources terminated",
                    cancellationToken);

                if (run is null)
                {
                    continue;
                }

                recovered++;
                _logger.LogWarning(
                    "AgentRunOrphanRecovered AgentRunId={AgentRunId} ProjectId={ProjectId} WorkItemId={WorkItemId} LastHeartbeatUtc={LastHeartbeatUtc:o}",
                    run.Id, run.ProjectId, run.WorkItemId, candidate.UpdatedAtUtc);

                // 2. Terminate the old runtime BEFORE the WorkItem can be released for a retry (3).
                foreach (var runtime in _runtimes)
                {
                    await runtime.EnsureCleanupAsync(run.Id, cancellationToken);
                }

                await _workItemSync.SyncAsync(run, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad candidate must not starve the others (or stop Worker startup). A run marked but not
                // yet synced is picked up by the WorkItem stale sweep; the container/process sweep below and
                // the next cycle retry the cleanup.
                _logger.LogError(ex, "AgentRunOrphanRecoveryFailed AgentRunId={AgentRunId}", candidate.Id);
            }
        }

        foreach (var runtime in _runtimes)
        {
            try
            {
                var swept = await runtime.SweepOrphanContainersAsync(cancellationToken);
                if (swept > 0)
                {
                    _logger.LogWarning("OrphanRuntimeResourcesSwept Runtime={Runtime} Count={Count}", runtime.GetType().Name, swept);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "OrphanRuntimeSweepFailed Runtime={Runtime}", runtime.GetType().Name);
            }
        }

        return recovered;
    }
}
