using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// The single entry point for starting a real end-to-end pipeline run — the
/// API layer must never coordinate context/planning/model/agent/build/test
/// itself; it only calls StartAsync and returns. Actual execution happens
/// on the Worker (see OrchestrationRunWorker), driven by the row this
/// creates.
/// </summary>
public interface IOrchestrationService
{
    Task<OrchestrationRunResult> StartAsync(
        StartOrchestrationCommand command,
        CancellationToken cancellationToken = default);

    Task<OrchestrationRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default);
}

public sealed record StartOrchestrationCommand(
    Guid ProjectId,
    Guid TaskId,
    int? MaxAttempts = null);

public sealed record OrchestrationRunResult(
    Guid RunId,
    string Status,
    string StatusUrl);
