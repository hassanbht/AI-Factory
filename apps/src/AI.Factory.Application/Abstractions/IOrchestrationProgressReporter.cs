using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Decouples AutonomousCodingOrchestrator (and anything else driving a
/// WorkItem's execution) from knowing OrchestrationRun exists at all — it
/// just reports "this WorkItem reached this stage," and the implementation
/// decides where that goes (see OrchestrationProgressReporter, which finds
/// the active run for the WorkItem and persists an OrchestrationProgressEvent).
/// </summary>
public interface IOrchestrationProgressReporter
{
    Task ReportAsync(
        Guid workItemId,
        OrchestrationProgressStage stage,
        string message,
        CancellationToken cancellationToken = default);
}

/// <summary>Default for every existing/optional caller that doesn't care about progress tracking.</summary>
public sealed class NullOrchestrationProgressReporter : IOrchestrationProgressReporter
{
    public static readonly NullOrchestrationProgressReporter Instance = new();

    public Task ReportAsync(
        Guid workItemId,
        OrchestrationProgressStage stage,
        string message,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
