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

    /// <summary>How often the Worker's periodic sweep checks for stale executions.</summary>
    public TimeSpan PeriodicSweepInterval { get; set; } = TimeSpan.FromMinutes(5);
}
