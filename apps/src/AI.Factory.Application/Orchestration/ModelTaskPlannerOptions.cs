namespace AI.Factory.Application.Orchestration;

public sealed class ModelTaskPlannerOptions
{
    /// <summary>Bounded retry on malformed/invalid planner output. Never unbounded.</summary>
    public int MaxAttempts { get; set; } = 3;
}
