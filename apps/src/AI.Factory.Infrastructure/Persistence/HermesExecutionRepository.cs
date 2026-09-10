using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class HermesExecutionRepository : IHermesExecutionRepository
{
    private readonly FactoryDbContext _db;

    public HermesExecutionRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task CreatePendingAsync(
        HermesExecution execution,
        CancellationToken cancellationToken = default)
    {
        await _db.HermesExecutions.AddAsync(execution, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        Guid executionId,
        bool success,
        string? output,
        string? error,
        long durationMs,
        CancellationToken cancellationToken = default)
    {
        var execution = await _db.HermesExecutions
            .FirstOrDefaultAsync(x => x.Id == executionId, cancellationToken);

        if (execution is null)
        {
            throw new NotFoundException($"HermesExecution '{executionId}' was not found.");
        }

        execution.Success = success;
        execution.Response = output;
        execution.Error = error;
        execution.DurationMs = durationMs;
        execution.CompletedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<HermesExecution?> GetLatestAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default)
    {
        return await _db.HermesExecutions
            .Where(x => x.WorkItemId == workItemId)
            .OrderByDescending(x => x.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
