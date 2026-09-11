namespace AI.Factory.Application.Agents;

/// <summary>Bound from configuration section "AgentWorker" — mirrors OrchestrationOptions' shape/validation convention.</summary>
public sealed class AgentWorkerOptions
{
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Phase 18.4 — how often a WorkItem linked to the AgentRun being
    /// executed gets its LastHeartbeatAtUtc refreshed. Must stay well below
    /// RecoveryOptions.StaleExecutionTimeout (default 15 min) so a genuinely
    /// running execution is never mistaken for stale; the 30s default gives
    /// a 30x margin against that default.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        var failures = new List<string>();

        if (PollingInterval <= TimeSpan.Zero) failures.Add($"{nameof(PollingInterval)} must be positive.");
        if (ExecutionTimeout <= TimeSpan.Zero) failures.Add($"{nameof(ExecutionTimeout)} must be positive.");
        if (HeartbeatInterval <= TimeSpan.Zero) failures.Add($"{nameof(HeartbeatInterval)} must be positive.");

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Invalid {nameof(AgentWorkerOptions)}: {string.Join(" ", failures)}");
        }
    }
}
