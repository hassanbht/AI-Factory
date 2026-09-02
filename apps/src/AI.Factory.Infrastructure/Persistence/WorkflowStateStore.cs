using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class WorkflowStateStore : IWorkflowStateStore
{
    private readonly FactoryDbContext _db;

    public WorkflowStateStore(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task<ProjectStatus> GetCurrentStatusAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);

        if (project is null)
        {
            throw new NotFoundException(
                $"Project '{projectId}' was not found.");
        }

        return project.Status;
    }

    public async Task ApplyTransitionAsync(
        WorkflowTransition transition,
        CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(x => x.Id == transition.ProjectId, cancellationToken);

        if (project is null)
        {
            throw new NotFoundException(
                $"Project '{transition.ProjectId}' was not found.");
        }

        project.Status = transition.ToStatus;
        project.UpdatedAtUtc = DateTime.UtcNow;

        await _db.WorkflowTransitions.AddAsync(transition, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }
}
