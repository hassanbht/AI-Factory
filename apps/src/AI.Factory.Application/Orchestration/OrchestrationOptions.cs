namespace AI.Factory.Application.Orchestration;

public sealed class OrchestrationOptions
{
    /// <summary>Every run workspace ("runs/{runId}/workspace") must resolve inside this root — enforced via WorkspacePathGuard.</summary>
    public string WorkspaceRoot { get; set; } = string.Empty;

    public int MaxAttempts { get; set; } = 3;

    public TimeSpan QueuePollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public void Validate()
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(WorkspaceRoot)) failures.Add($"{nameof(WorkspaceRoot)} must be configured.");
        if (MaxAttempts <= 0) failures.Add($"{nameof(MaxAttempts)} must be positive.");
        if (QueuePollingInterval <= TimeSpan.Zero) failures.Add($"{nameof(QueuePollingInterval)} must be positive.");
        if (ExecutionTimeout <= TimeSpan.Zero) failures.Add($"{nameof(ExecutionTimeout)} must be positive.");

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Invalid {nameof(OrchestrationOptions)}: {string.Join(" ", failures)}");
        }
    }
}
