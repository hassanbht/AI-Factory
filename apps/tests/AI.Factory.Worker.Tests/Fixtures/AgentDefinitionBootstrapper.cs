using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Planning;
using AI.Factory.Domain.Enums;
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

        // Phase 18.23 §10/§11 — one definition per WorkItemType, plus the two
        // roles the Factory looks up by their own convention (Planner, Repair).
        // TaskDispatchScheduler matches AgentType == WorkItem.Type.ToString(),
        // and a REAL Planner chooses the task types itself: registering only
        // the handful a fixture happens to use would leave a perfectly valid
        // plan undispatchable for reasons that have nothing to do with the plan.
        // Registering an agent per type is the operator action the Factory has
        // always assumed, not a relaxation of anything.
        var requiredTypes = Enum.GetNames<WorkItemType>()
            .Select(t => (AgentType: t, Description: $"Agent for {t} work items"))
            .Concat(new[]
            {
                (AgentType: ProjectPlanner.PlannerAgentType,
                    Description: "System planner agent for project plan decomposition"),
                (AgentType: TaskDispatchScheduler.RepairAgentType,
                    Description: "Repair agent for diagnosing and fixing verification failures")
            })
            .DistinctBy(x => x.AgentType, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
