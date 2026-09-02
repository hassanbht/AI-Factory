namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Centralized, configurable ceilings for every long-running or bounded
/// operation in the pipeline — the single source of truth so no timeout or
/// retry-count value is hardcoded at an individual call site.
/// </summary>
public sealed class ExecutionLimitsOptions
{
    public int ModelRequestTimeoutSeconds { get; set; } = 30;

    public int AgentExecutionTimeoutMinutes { get; set; } = 5;

    public int BuildTimeoutMinutes { get; set; } = 5;

    public int TestTimeoutMinutes { get; set; } = 10;

    public int MaxRepairAttempts { get; set; } = 3;

    public int MaxWorkflowDurationMinutes { get; set; } = 30;

    /// <summary>Pure predicate (no real waiting needed to test it) backing the orchestrator's wall-clock checkpoints.</summary>
    public bool ExceedsWorkflowDuration(TimeSpan elapsed) =>
        elapsed > TimeSpan.FromMinutes(MaxWorkflowDurationMinutes);

    /// <summary>Fail fast at startup rather than surfacing a confusing timeout mid-pipeline.</summary>
    public void Validate()
    {
        var failures = new List<string>();

        if (ModelRequestTimeoutSeconds <= 0) failures.Add($"{nameof(ModelRequestTimeoutSeconds)} must be positive.");
        if (AgentExecutionTimeoutMinutes <= 0) failures.Add($"{nameof(AgentExecutionTimeoutMinutes)} must be positive.");
        if (BuildTimeoutMinutes <= 0) failures.Add($"{nameof(BuildTimeoutMinutes)} must be positive.");
        if (TestTimeoutMinutes <= 0) failures.Add($"{nameof(TestTimeoutMinutes)} must be positive.");
        if (MaxRepairAttempts <= 0) failures.Add($"{nameof(MaxRepairAttempts)} must be positive.");
        if (MaxWorkflowDurationMinutes <= 0) failures.Add($"{nameof(MaxWorkflowDurationMinutes)} must be positive.");

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Invalid {nameof(ExecutionLimitsOptions)}: {string.Join(" ", failures)}");
        }
    }
}
