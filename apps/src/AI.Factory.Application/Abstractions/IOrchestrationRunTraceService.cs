using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Same decoupling as IOrchestrationProgressReporter: the orchestrator/engine
/// report "this WorkItem's stage started/completed/failed/was skipped," and
/// the implementation (OrchestrationRunTraceService) decides where that goes
/// — finding the active run for the WorkItem and persisting an
/// OrchestrationRunStep — without Application code ever referencing EF Core.
/// </summary>
public interface IOrchestrationRunTraceService
{
    Task StartStepAsync(
        Guid workItemId,
        OrchestrationStepKind stage,
        int? attemptNumber = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    Task CompleteStepAsync(
        Guid workItemId,
        OrchestrationStepKind stage,
        int? attemptNumber = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    Task FailStepAsync(
        Guid workItemId,
        OrchestrationStepKind stage,
        string failureType,
        string failureMessage,
        int? attemptNumber = null,
        CancellationToken cancellationToken = default);

    Task SkipStepAsync(
        Guid workItemId,
        OrchestrationStepKind stage,
        string reason,
        int? attemptNumber = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Default for every existing/optional caller that doesn't care about step-level tracing.</summary>
public sealed class NullOrchestrationRunTraceService : IOrchestrationRunTraceService
{
    public static readonly NullOrchestrationRunTraceService Instance = new();

    public Task StartStepAsync(
        Guid workItemId, OrchestrationStepKind stage, int? attemptNumber = null,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CompleteStepAsync(
        Guid workItemId, OrchestrationStepKind stage, int? attemptNumber = null,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task FailStepAsync(
        Guid workItemId, OrchestrationStepKind stage, string failureType, string failureMessage,
        int? attemptNumber = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SkipStepAsync(
        Guid workItemId, OrchestrationStepKind stage, string reason,
        int? attemptNumber = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
