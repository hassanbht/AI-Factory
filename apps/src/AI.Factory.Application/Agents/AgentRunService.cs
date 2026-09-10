using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Agents;

/// <summary>
/// The application-layer use-case behind the AgentRun API. Creates a run in
/// Queued state (never executes synchronously — the Worker discovers it),
/// resolves query/detail reads, and handles cancellation before/between
/// execution. Never touches EF directly — everything goes through
/// IAgentRunRepository/IAgentDefinitionRepository.
/// </summary>
public interface IAgentRunService
{
    Task<AgentRun> CreateAsync(CreateAgentRunCommand command, CancellationToken cancellationToken = default);

    Task<AgentRun?> GetAsync(Guid agentRunId, CancellationToken cancellationToken = default);

    Task<AgentRunPage> ListAsync(AgentRunQuery query, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid agentRunId, CancellationToken cancellationToken = default);
}

public sealed record CreateAgentRunCommand(Guid AgentDefinitionId, Guid? ProjectId, string? Input);

public sealed class AgentRunService : IAgentRunService
{
    private readonly IAgentRunRepository _runRepository;
    private readonly IAgentDefinitionRepository _definitionRepository;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<AgentRunService> _logger;

    public AgentRunService(
        IAgentRunRepository runRepository,
        IAgentDefinitionRepository definitionRepository,
        IOperationalMetrics metrics,
        ILogger<AgentRunService> logger)
    {
        _runRepository = runRepository;
        _definitionRepository = definitionRepository;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<AgentRun> CreateAsync(
        CreateAgentRunCommand command, CancellationToken cancellationToken = default)
    {
        if (command.AgentDefinitionId == Guid.Empty)
        {
            throw new ValidationException("AgentDefinitionId must be a non-empty GUID.");
        }

        var definition = await _definitionRepository.GetByIdAsync(command.AgentDefinitionId, cancellationToken)
            ?? throw new NotFoundException($"AgentDefinition '{command.AgentDefinitionId}' was not found.");

        if (!definition.Enabled)
        {
            throw new ValidationException($"AgentDefinition '{definition.Id}' is disabled.");
        }

        var run = new AgentRun
        {
            AgentDefinitionId = definition.Id,
            ProjectId = command.ProjectId,
            Input = command.Input
        };

        run = await _runRepository.CreateAsync(run, cancellationToken);

        // CreateAsync is idempotent on CorrelationId and may return a
        // pre-existing row already past Created — only a genuinely fresh
        // row needs the Created -> Queued transition applied here.
        if (run.Status == AgentRunStatus.Created)
        {
            run.TransitionTo(AgentRunStatus.Queued);
            await _runRepository.UpdateAsync(run, cancellationToken);
        }

        _metrics.RecordAgentRunCreated();

        _logger.LogInformation(
            "AgentRunCreated AgentRunId={AgentRunId} AgentDefinitionId={AgentDefinitionId} ProjectId={ProjectId} CorrelationId={CorrelationId}",
            run.Id, run.AgentDefinitionId, run.ProjectId, run.CorrelationId);

        return run;
    }

    public Task<AgentRun?> GetAsync(Guid agentRunId, CancellationToken cancellationToken = default) =>
        _runRepository.GetByIdAsync(agentRunId, cancellationToken);

    public Task<AgentRunPage> ListAsync(AgentRunQuery query, CancellationToken cancellationToken = default) =>
        _runRepository.GetPagedAsync(query, cancellationToken);

    public async Task CancelAsync(Guid agentRunId, CancellationToken cancellationToken = default)
    {
        var run = await _runRepository.GetByIdAsync(agentRunId, cancellationToken)
            ?? throw new NotFoundException($"AgentRun '{agentRunId}' was not found.");

        if (AgentRunTransitions.IsTerminal(run.Status))
        {
            throw new ConflictException(
                $"AgentRun '{agentRunId}' is already in terminal state '{run.Status}' and cannot be cancelled.");
        }

        if (run.Status is AgentRunStatus.Running or AgentRunStatus.Created)
        {
            // Running: no cooperative mid-flight cancellation signal exists
            // yet between this API and AgentOrchestrator/IAgentRuntime —
            // flipping Status here without the runtime observing it would
            // create an AgentRun the orchestrator's own completion path
            // could no longer legally transition out of. Created: the
            // domain requires Queued first; a Created row this API produced
            // should never observably exist outside CreateAsync's own call.
            throw new ConflictException(
                $"AgentRun '{agentRunId}' is currently '{run.Status}' and cannot be cancelled through this API yet.");
        }

        run.TransitionTo(AgentRunStatus.Cancelled);
        await _runRepository.UpdateAsync(run, cancellationToken);

        _metrics.RecordAgentRunCancelled();
        _logger.LogInformation(
            "AgentRunCancelled AgentRunId={AgentRunId} CorrelationId={CorrelationId}", run.Id, run.CorrelationId);
    }
}
