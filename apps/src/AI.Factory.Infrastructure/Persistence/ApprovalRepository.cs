using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class ApprovalRepository : IApprovalRepository
{
    private readonly FactoryDbContext _db;

    public ApprovalRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task<ApprovalRequest> CreateAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        await _db.ApprovalRequests.AddAsync(request, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return request;
    }

    public async Task<IReadOnlyList<ApprovalRequest>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        return await _db.ApprovalRequests
            .Where(x => x.Decision == ApprovalDecision.PendingApproval)
            .OrderBy(x => x.RequestedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<ApprovalRequest?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _db.ApprovalRequests
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task DecideAsync(
        Guid id,
        ApprovalDecision decision,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        var request = await _db.ApprovalRequests
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (request is null)
        {
            throw new NotFoundException($"ApprovalRequest '{id}' was not found.");
        }

        if (request.Decision != ApprovalDecision.PendingApproval)
        {
            throw new ConflictException(
                $"ApprovalRequest '{id}' was already decided ({request.Decision}).");
        }

        request.Decision = decision;
        request.Comment = comment;
        request.DecidedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ApprovalRequest?> GetLatestForProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return await _db.ApprovalRequests
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.RequestedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
