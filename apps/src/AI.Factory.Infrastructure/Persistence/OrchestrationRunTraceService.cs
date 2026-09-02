using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Persistence;

/// <summary>
/// Persists the OrchestrationRunStep trace. Every method is best-effort and
/// non-throwing (matches OrchestrationProgressReporter): a trace-persistence
/// failure is logged and swallowed here, never allowed to fail the caller's
/// real orchestration work, and never silently invented — if there is no
/// active run, or no matching open step to complete/fail, nothing is written.
/// </summary>
public sealed class OrchestrationRunTraceService : IOrchestrationRunTraceService
{
    private readonly IOrchestrationRunRepository _runRepository;
    private readonly ILogger<OrchestrationRunTraceService> _logger;

    public OrchestrationRunTraceService(
        IOrchestrationRunRepository runRepository, ILogger<OrchestrationRunTraceService> logger)
    {
        _runRepository = runRepository;
        _logger = logger;
    }

    public async Task StartStepAsync(
        Guid workItemId,
        OrchestrationStepKind stage,
        int? attemptNumber = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var run = await _runRepository.GetActiveByWorkItemIdAsync(workItemId, cancellationToken);

        if (run is null)
        {
            return;
        }

        try
        {
            await _runRepository.AddStepAsync(new OrchestrationRunStep
            {
                Id = Guid.NewGuid(),
                OrchestrationRunId = run.Id,
                Stage = stage,
                Status = OrchestrationStepStatus.Running,
                AttemptNumber = attemptNumber,
                StartedAtUtc = DateTime.UtcNow,
                MetadataJson = Serialize(metadata)
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist trace step start for run {RunId}, stage {Stage}", run.Id, stage);
        }
    }

    public async Task CompleteStepAsync(
        Guid workItemId,
        OrchestrationStepKind stage,
        int? attemptNumber = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var run = await _runRepository.GetActiveByWorkItemIdAsync(workItemId, cancellationToken);

        if (run is null)
        {
            return;
        }

        try
        {
            var step = await _runRepository.FindLatestOpenStepAsync(run.Id, stage, attemptNumber, cancellationToken);

            if (step is null)
            {
                return;
            }

            step.Status = OrchestrationStepStatus.Succeeded;
            step.CompletedAtUtc = DateTime.UtcNow;

            if (metadata is not null)
            {
                step.MetadataJson = Serialize(metadata);
            }

            await _runRepository.UpdateStepAsync(step, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist trace step completion for run {RunId}, stage {Stage}", run.Id, stage);
        }
    }

    public async Task FailStepAsync(
        Guid workItemId,
        OrchestrationStepKind stage,
        string failureType,
        string failureMessage,
        int? attemptNumber = null,
        CancellationToken cancellationToken = default)
    {
        var run = await _runRepository.GetActiveByWorkItemIdAsync(workItemId, cancellationToken);

        if (run is null)
        {
            return;
        }

        try
        {
            var step = await _runRepository.FindLatestOpenStepAsync(run.Id, stage, attemptNumber, cancellationToken);

            if (step is null)
            {
                return;
            }

            step.Status = OrchestrationStepStatus.Failed;
            step.CompletedAtUtc = DateTime.UtcNow;
            step.FailureType = failureType;
            step.FailureMessage = failureMessage;

            await _runRepository.UpdateStepAsync(step, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist trace step failure for run {RunId}, stage {Stage}", run.Id, stage);
        }
    }

    public async Task SkipStepAsync(
        Guid workItemId,
        OrchestrationStepKind stage,
        string reason,
        int? attemptNumber = null,
        CancellationToken cancellationToken = default)
    {
        var run = await _runRepository.GetActiveByWorkItemIdAsync(workItemId, cancellationToken);

        if (run is null)
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;

            await _runRepository.AddStepAsync(new OrchestrationRunStep
            {
                Id = Guid.NewGuid(),
                OrchestrationRunId = run.Id,
                Stage = stage,
                Status = OrchestrationStepStatus.Skipped,
                AttemptNumber = attemptNumber,
                StartedAtUtc = now,
                CompletedAtUtc = now,
                FailureMessage = reason
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist trace step skip for run {RunId}, stage {Stage}", run.Id, stage);
        }
    }

    private static string? Serialize(IReadOnlyDictionary<string, string>? metadata)
        => metadata is null || metadata.Count == 0 ? null : JsonSerializer.Serialize(metadata);
}
