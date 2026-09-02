namespace AI.Factory.Application.Orchestration;

public sealed class AuditOptions
{
    /// <summary>How long an audit log entry is kept before PurgeOlderThanAsync considers it eligible for deletion.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>When true, a failure to persist an audit entry propagates instead of being logged-and-swallowed.</summary>
    public bool FailOnAuditError { get; set; }
}
