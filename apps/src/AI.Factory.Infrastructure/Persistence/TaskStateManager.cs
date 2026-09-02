using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class TaskStateManager : ITaskStateManager
{
    private readonly FactoryDbContext _db;
    private readonly ExecutionLimitsOptions _executionLimits;
    private readonly RecoveryOptions _recoveryOptions;

    public TaskStateManager(
        FactoryDbContext db,
        ExecutionLimitsOptions? executionLimits = null,
        RecoveryOptions? recoveryOptions = null)
    {
        _db = db;
        _executionLimits = executionLimits ?? new ExecutionLimitsOptions();
        _recoveryOptions = recoveryOptions ?? new RecoveryOptions();
    }

    /// <summary>
    /// A global ceiling on top of the per-WorkItem MaxAttempts — whichever
    /// is smaller wins, so a misconfigured/inflated per-task value can never
    /// exceed the operator's configured repair budget.
    /// </summary>
    private int EffectiveMaxAttempts(WorkItem item) =>
        Math.Min(item.MaxAttempts, _executionLimits.MaxRepairAttempts);

    public async Task<bool> TryStartAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default)
    {
        var item = await GetOrThrow(workItemId, cancellationToken);

        if (item.Status == WorkItemStatus.Running.ToString())
        {
            return false;
        }

        // ponytail: read-then-write, not a DB-level compare-and-swap — fine
        // while concurrency = 1 / single process. Add a conditional UPDATE
        // (rows-affected check) if concurrent schedulers are introduced later.
        item.Status = WorkItemStatus.Running.ToString();
        item.StartedAtUtc = DateTime.UtcNow;
        item.LastHeartbeatAtUtc = DateTime.UtcNow;
        item.LastCheckpoint = null;
        item.AttemptCount += 1;
        item.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task CompleteAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default)
    {
        var item = await GetOrThrow(workItemId, cancellationToken);

        item.Status = WorkItemStatus.Completed.ToString();
        item.CompletedAtUtc = DateTime.UtcNow;
        item.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> FailAsync(
        Guid workItemId,
        string error,
        CancellationToken cancellationToken = default)
    {
        var item = await GetOrThrow(workItemId, cancellationToken);

        item.CurrentError = error;
        item.UpdatedAtUtc = DateTime.UtcNow;

        var exhausted = item.AttemptCount >= EffectiveMaxAttempts(item);

        item.Status = exhausted
            ? WorkItemStatus.Failed.ToString()
            : WorkItemStatus.Repairing.ToString();

        await _db.SaveChangesAsync(cancellationToken);

        return exhausted;
    }

    public async Task RecoverOrphanedTasksAsync(CancellationToken cancellationToken = default)
    {
        var orphaned = await _db.WorkItems
            .Where(x => x.Status == WorkItemStatus.Running.ToString())
            .ToListAsync(cancellationToken);

        foreach (var item in orphaned)
        {
            RecoverRunningItem(item, "Recovered after process restart — execution did not complete.");
        }

        if (orphaned.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task HeartbeatAsync(
        Guid workItemId,
        string? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        var item = await GetOrThrow(workItemId, cancellationToken);

        item.LastHeartbeatAtUtc = DateTime.UtcNow;

        if (checkpoint is not null)
        {
            item.LastCheckpoint = checkpoint;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RecoverStaleExecutionsAsync(CancellationToken cancellationToken = default)
    {
        var cutoffUtc = DateTime.UtcNow - _recoveryOptions.StaleExecutionTimeout;

        // Only a Running item whose most recent proof of liveness — the
        // heartbeat, or StartedAtUtc if one was never recorded — predates
        // the cutoff is stale. A genuinely still-executing item (recent
        // heartbeat) is left completely untouched.
        var stale = await _db.WorkItems
            .Where(x => x.Status == WorkItemStatus.Running.ToString())
            .Where(x => (x.LastHeartbeatAtUtc ?? x.StartedAtUtc ?? DateTime.MinValue) < cutoffUtc)
            .ToListAsync(cancellationToken);

        foreach (var item in stale)
        {
            RecoverRunningItem(
                item,
                $"Recovered — no heartbeat within the configured {_recoveryOptions.StaleExecutionTimeout} stale-execution timeout.");
        }

        if (stale.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return stale.Count;
    }

    /// <summary>
    /// Shared by both recovery paths: same attempts-remaining-vs-exhausted
    /// transition as FailAsync, but never re-executes anything itself —
    /// it only updates status, deferring any actual re-run to the normal
    /// scheduler/orchestrator pickup path on its next pass (which always
    /// starts a fresh, isolated agent workspace), so recovery can never
    /// itself duplicate a side-effecting operation.
    /// </summary>
    private void RecoverRunningItem(WorkItem item, string reason)
    {
        item.CurrentError = reason;
        item.UpdatedAtUtc = DateTime.UtcNow;

        item.Status = item.AttemptCount < EffectiveMaxAttempts(item)
            ? WorkItemStatus.Repairing.ToString()
            : WorkItemStatus.Failed.ToString();
    }

    private async Task<WorkItem> GetOrThrow(
        Guid workItemId,
        CancellationToken cancellationToken)
    {
        var item = await _db.WorkItems
            .FirstOrDefaultAsync(x => x.Id == workItemId, cancellationToken);

        if (item is null)
        {
            throw new InvalidOperationException(
                $"WorkItem '{workItemId}' was not found.");
        }

        return item;
    }
}
