namespace AI.Factory.Application.Orchestration;

public sealed class RecoveryOptions
{
    /// <summary>
    /// A Running WorkItem whose LastHeartbeatAtUtc (falling back to
    /// StartedAtUtc if a heartbeat was never recorded) is older than this is
    /// considered stale — the process behind it is presumed dead even
    /// though the current process never restarted.
    /// </summary>
    public TimeSpan StaleExecutionTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Phase M — a Running AgentRun with no heartbeat for this long is orphaned: its Worker is dead.
    /// Every executing AgentRun is heartbeated every AgentWorker:HeartbeatInterval (30s), so the
    /// default tolerates three missed beats. Replaces StaleExecutionTimeout for AgentRuns, which let a
    /// crashed Worker's container keep running for ~15 minutes next to its replacement (benchmark
    /// factory-T3-r1). Must exceed twice the heartbeat interval.
    /// </summary>
    public TimeSpan OrphanHeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the Worker's periodic sweep checks for stale executions.</summary>
    public TimeSpan PeriodicSweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    public void Validate(TimeSpan heartbeatInterval)
    {
        if (OrphanHeartbeatTimeout <= heartbeatInterval * 2)
        {
            throw new InvalidOperationException(
                $"Invalid {nameof(RecoveryOptions)}: {nameof(OrphanHeartbeatTimeout)} ({OrphanHeartbeatTimeout}) must exceed "
                + $"twice the AgentWorker heartbeat interval ({heartbeatInterval}), or live runs would be recovered as orphans.");
        }

        if (PeriodicSweepInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Invalid {nameof(RecoveryOptions)}: {nameof(PeriodicSweepInterval)} must be positive.");
        }
    }
}
