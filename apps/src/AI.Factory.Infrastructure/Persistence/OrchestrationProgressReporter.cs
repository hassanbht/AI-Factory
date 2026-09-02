using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class OrchestrationProgressReporter : IOrchestrationProgressReporter
{
    private readonly IOrchestrationRunRepository _runRepository;
    private readonly ILogger<OrchestrationProgressReporter> _logger;

    public OrchestrationProgressReporter(
        IOrchestrationRunRepository runRepository, ILogger<OrchestrationProgressReporter> logger)
    {
        _runRepository = runRepository;
        _logger = logger;
    }

    public async Task ReportAsync(
        Guid workItemId,
        OrchestrationProgressStage stage,
        string message,
        CancellationToken cancellationToken = default)
    {
        var run = await _runRepository.GetActiveByWorkItemIdAsync(workItemId, cancellationToken);

        if (run is null)
        {
            // No OrchestrationRun is driving this WorkItem right now (e.g.
            // it's running through a different trigger path) — nothing to
            // attach the event to. Never fail the caller's own workflow
            // over an observability side-channel.
            return;
        }

        try
        {
            await _runRepository.AddProgressEventAsync(new OrchestrationProgressEvent
            {
                Id = Guid.NewGuid(),
                OrchestrationRunId = run.Id,
                Stage = stage,
                TimestampUtc = DateTime.UtcNow,
                Message = message
            }, cancellationToken);

            run.CurrentStage = stage;
            await _runRepository.UpdateAsync(run, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist progress event for run {RunId}, stage {Stage}", run.Id, stage);
        }
    }
}
