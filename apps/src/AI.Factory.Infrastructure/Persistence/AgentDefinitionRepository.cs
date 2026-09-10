using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class AgentDefinitionRepository : IAgentDefinitionRepository
{
    private readonly FactoryDbContext _db;

    public AgentDefinitionRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task<AgentDefinition> CreateAsync(
        AgentDefinition agentDefinition,
        CancellationToken cancellationToken = default)
    {
        await _db.AgentDefinitions.AddAsync(agentDefinition, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return agentDefinition;
    }

    public async Task<AgentDefinition?> GetByIdAsync(
        Guid agentDefinitionId,
        CancellationToken cancellationToken = default)
    {
        return await _db.AgentDefinitions
            .FirstOrDefaultAsync(x => x.Id == agentDefinitionId, cancellationToken);
    }

    public async Task UpdateAsync(
        AgentDefinition agentDefinition,
        CancellationToken cancellationToken = default)
    {
        var exists = await _db.AgentDefinitions
            .AnyAsync(x => x.Id == agentDefinition.Id, cancellationToken);

        if (!exists)
        {
            throw new NotFoundException($"AgentDefinition '{agentDefinition.Id}' was not found.");
        }

        agentDefinition.UpdatedAtUtc = DateTime.UtcNow;
        _db.AgentDefinitions.Update(agentDefinition);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentDefinition>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await _db.AgentDefinitions
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }
}
