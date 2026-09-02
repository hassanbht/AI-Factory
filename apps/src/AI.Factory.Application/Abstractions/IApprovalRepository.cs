using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

public interface IApprovalRepository
{
    Task<ApprovalRequest> CreateAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApprovalRequest>> GetPendingAsync(
        CancellationToken cancellationToken = default);

    Task<ApprovalRequest?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task DecideAsync(
        Guid id,
        ApprovalDecision decision,
        string? comment,
        CancellationToken cancellationToken = default);

    /// <summary>Most recent approval request for a project, or null if none was ever raised.</summary>
    Task<ApprovalRequest?> GetLatestForProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}
