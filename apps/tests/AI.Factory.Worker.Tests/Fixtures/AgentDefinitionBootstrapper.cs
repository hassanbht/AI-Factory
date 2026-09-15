using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Persistence;

namespace AI.Factory.Worker.Tests.Fixtures;

/// <summary>
/// Phase 18.18 — Deterministic bootstrap and seeding mechanism for
/// AgentDefinition records required by Golden Path and E2E acceptance tests.
/// Ensures tests do not depend on undocumented or manually created database state.
/// </summary>
public static class AgentDefinitionBootstrapper
{
    public static async Task EnsureBootstrapDefinitionsAsync(FactoryDbContext db, CancellationToken cancellationToken = default)
    {
        var repo = new AgentDefinitionRepository(db);
        var existing = await repo.GetAllAsync(cancellationToken);

        var requiredTypes = new[]
        {
            ("Planner", "System planner agent for project plan decomposition"),
            ("Implementation", "Coding agent for software implementation tasks"),
            ("Repair", "Repair agent for diagnosing and fixing verification failures"),
            ("Test", "Testing agent for test generation and verification"),
            ("Review", "Review agent for independent code review")
        };

        foreach (var (agentType, description) in requiredTypes)
        {
            if (!existing.Any(x => string.Equals(x.AgentType, agentType, StringComparison.OrdinalIgnoreCase)))
            {
                await repo.CreateAsync(new AgentDefinition
                {
                    Name = $"Golden-{agentType}-Agent",
                    AgentType = agentType,
                    Description = description,
                    SystemPrompt = $"You are an autonomous {agentType} agent.",
                    Enabled = true
                }, cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
