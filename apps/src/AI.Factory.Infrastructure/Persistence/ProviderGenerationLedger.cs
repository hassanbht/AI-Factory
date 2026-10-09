using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

/// <summary>Phase Q.3 — EF Core durable ledger for authoritative provider generations.</summary>
public sealed partial class ProviderGenerationLedger : IProviderGenerationLedger
{
    private readonly FactoryDbContext _db;

    public ProviderGenerationLedger(FactoryDbContext db) => _db = db;

    public async Task<ProviderGeneration> RecordGenerationAsync(
        ProviderGeneration generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (!Application.CostAccounting.OpenRouterGenerationCapture.IsWellFormedGenerationId(generation.GenerationId))
        {
            throw new ArgumentException("Generation id is malformed; refusing to record.", nameof(generation));
        }

        var existing = await _db.ProviderGenerations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.GenerationId == generation.GenerationId, cancellationToken);
        if (existing is not null)
        {
            if (existing.AgentRunId != generation.AgentRunId
                || !string.Equals(existing.BenchmarkIdentity, generation.BenchmarkIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Generation already bound elsewhere; refusing silent reassignment.");
            }

            return existing;
        }

        generation.CreatedAtUtc = DateTime.UtcNow;
        generation.UpdatedAtUtc = DateTime.UtcNow;
        _db.ProviderGenerations.Add(generation);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var raced = await _db.ProviderGenerations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.GenerationId == generation.GenerationId, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            if (raced.AgentRunId != generation.AgentRunId
                || !string.Equals(raced.BenchmarkIdentity, generation.BenchmarkIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Generation raced and is bound elsewhere; refusing reassignment.");
            }

            return raced;
        }

        return generation;
    }

    public Task<ProviderGeneration?> GetByGenerationIdAsync(string generationId, CancellationToken ct = default) =>
        _db.ProviderGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.GenerationId == generationId, ct);

    public async Task<IReadOnlyList<ProviderGeneration>> ListByAgentRunAsync(Guid agentRunId, CancellationToken ct = default) =>
        await _db.ProviderGenerations.AsNoTracking()
            .Where(x => x.AgentRunId == agentRunId).OrderBy(x => x.RequestedAtUtc).ToListAsync(ct);
}
