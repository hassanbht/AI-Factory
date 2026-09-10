namespace AI.Factory.Application.Agents;

/// <summary>Bound from configuration section "AgentWorker" — mirrors OrchestrationOptions' shape/validation convention.</summary>
public sealed class AgentWorkerOptions
{
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        var failures = new List<string>();

        if (PollingInterval <= TimeSpan.Zero) failures.Add($"{nameof(PollingInterval)} must be positive.");
        if (ExecutionTimeout <= TimeSpan.Zero) failures.Add($"{nameof(ExecutionTimeout)} must be positive.");

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Invalid {nameof(AgentWorkerOptions)}: {string.Join(" ", failures)}");
        }
    }
}
