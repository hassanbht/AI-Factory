namespace AI.Factory.Application.Configuration;

/// <summary>
/// Configurable dispatch capacity for <see cref="Orchestration.TaskDispatchScheduler"/>
/// — Phase 18.3 §11. Concurrency is scoped per project: at most MaxConcurrentTasks
/// WorkItems may be Running in a given project at once.
/// </summary>
public sealed class TaskSchedulerOptions
{
    public const string SectionName = "TaskScheduler";

    public bool Enabled { get; set; } = true;

    public int MaxConcurrentTasks { get; set; } = 1;

    public TimeSpan SchedulingInterval { get; set; } = TimeSpan.FromSeconds(15);
}
