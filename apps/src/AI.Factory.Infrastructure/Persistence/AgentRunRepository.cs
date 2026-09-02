using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class AgentRunRepository : IAgentRunRepository
{
    private readonly FactoryDbContext _db;

    public AgentRunRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task CreatePendingAsync(
        AgentRun agentRun,
        CancellationToken cancellationToken = default)
    {
        await _db.AgentRuns.AddAsync(agentRun, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        Guid agentRunId,
        bool success,
        string? output,
        string? error,
        long durationMs,
        CancellationToken cancellationToken = default)
    {
        var run = await _db.AgentRuns
            .FirstOrDefaultAsync(x => x.Id == agentRunId, cancellationToken);

        if (run is null)
        {
            throw new InvalidOperationException($"AgentRun '{agentRunId}' was not found.");
        }

        run.Success = success;
        run.Response = output;
        run.Error = error;
        run.DurationMs = durationMs;
        run.CompletedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AgentRun?> GetLatestAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default)
    {
        return await _db.AgentRuns
            .Where(x => x.WorkItemId == workItemId)
            .OrderByDescending(x => x.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
