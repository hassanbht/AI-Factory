using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Orchestration;

public sealed class OrchestrationService : IOrchestrationService
{
    private readonly IProjectRepository _projectRepository;
    private readonly IWorkItemRepository _workItemRepository;
    private readonly IOrchestrationRunRepository _runRepository;
    private readonly OrchestrationOptions _options;

    public OrchestrationService(
        IProjectRepository projectRepository,
        IWorkItemRepository workItemRepository,
        IOrchestrationRunRepository runRepository,
        OrchestrationOptions options)
    {
        _projectRepository = projectRepository;
        _workItemRepository = workItemRepository;
        _runRepository = runRepository;
        _options = options;
    }

    public async Task<OrchestrationRunResult> StartAsync(
        StartOrchestrationCommand command,
        CancellationToken cancellationToken = default)
    {
        var project = await _projectRepository.GetByIdAsync(command.ProjectId, cancellationToken)
            ?? throw new NotFoundException($"Project '{command.ProjectId}' was not found.");

        var workItem = await _workItemRepository.GetByIdAsync(command.TaskId, cancellationToken)
            ?? throw new NotFoundException($"Task '{command.TaskId}' was not found.");

        if (workItem.ProjectId != project.Id)
        {
            throw new ValidationException($"Task '{command.TaskId}' does not belong to project '{command.ProjectId}'.");
        }

        var run = new OrchestrationRun
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            WorkItemId = workItem.Id,
            Status = OrchestrationRunStatus.Pending,
            CurrentStage = OrchestrationProgressStage.RunCreated,
            RequestedAtUtc = DateTime.UtcNow,
            MaxAttempts = command.MaxAttempts ?? _options.MaxAttempts
        };

        var created = await _runRepository.CreateAsync(run, cancellationToken);

        await _runRepository.AddProgressEventAsync(new OrchestrationProgressEvent
        {
            Id = Guid.NewGuid(),
            OrchestrationRunId = created.Id,
            Stage = OrchestrationProgressStage.RunCreated,
            TimestampUtc = DateTime.UtcNow,
            Message = $"Run created for task '{workItem.Title}'."
        }, cancellationToken);

        return new OrchestrationRunResult(
            created.Id,
            created.Status.ToString(),
            $"/api/orchestration-runs/{created.Id}");
    }

    public Task<OrchestrationRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _runRepository.GetByIdAsync(runId, cancellationToken);

    public async Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _runRepository.GetByIdAsync(runId, cancellationToken)
            ?? throw new NotFoundException($"OrchestrationRun '{runId}' was not found.");

        if (run.Status is OrchestrationRunStatus.Completed or OrchestrationRunStatus.Failed or OrchestrationRunStatus.Cancelled)
        {
            throw new ConflictException($"OrchestrationRun '{runId}' is already terminal ({run.Status}) and cannot be cancelled.");
        }

        await _runRepository.RequestCancellationAsync(runId, cancellationToken);
    }
}
