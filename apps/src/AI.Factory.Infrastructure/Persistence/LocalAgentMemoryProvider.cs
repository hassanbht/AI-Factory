using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IAgentMemory"/> using the existing Factory
/// database — no separate store, no vector search. Lexical SearchText match
/// only; documented here rather than pretending at semantic relevance.
/// Enforces the requested scope itself: a query naming ProjectId/
/// AgentDefinitionId/AgentRunId only ever matches rows with that exact
/// scope value, never rows outside it.
/// </summary>
public sealed class LocalAgentMemoryProvider : IAgentMemory
{
    private readonly FactoryDbContext _db;

    public LocalAgentMemoryProvider(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task StoreAsync(AgentMemoryEntry entry, CancellationToken cancellationToken = default)
    {
        var record = new AgentMemoryRecord
        {
            Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
            ProjectId = entry.ProjectId,
            AgentDefinitionId = entry.AgentDefinitionId,
            AgentRunId = entry.AgentRunId,
            Type = entry.Type,
            Content = entry.Content,
            MetadataJson = entry.Metadata is null ? null : JsonSerializer.Serialize(entry.Metadata),
            CreatedAtUtc = entry.CreatedAtUtc == default ? DateTime.UtcNow : entry.CreatedAtUtc
        };

        await _db.AgentMemories.AddAsync(record, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentMemoryEntry>> SearchAsync(
        AgentMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var results = await _db.AgentMemories
            .Where(x => x.ProjectId == query.ProjectId)
            .Where(x => query.AgentDefinitionId == null || x.AgentDefinitionId == query.AgentDefinitionId)
            .Where(x => query.AgentRunId == null || x.AgentRunId == query.AgentRunId)
            .Where(x => query.Type == null || x.Type == query.Type)
            .Where(x => query.SearchText == null || EF.Functions.Like(x.Content, $"%{query.SearchText}%"))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .Take(query.MaxResults)
            .ToListAsync(cancellationToken);

        return results
            .Select(x => new AgentMemoryEntry(
                x.Id,
                x.ProjectId,
                x.AgentDefinitionId,
                x.AgentRunId,
                x.Type,
                x.Content,
                x.MetadataJson is null
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(x.MetadataJson),
                x.CreatedAtUtc))
            .ToList();
    }
}
