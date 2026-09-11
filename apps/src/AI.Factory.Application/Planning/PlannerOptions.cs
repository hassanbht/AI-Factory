namespace AI.Factory.Application.Planning;

/// <summary>
/// Phase 18.6 §19 — the smallest explicit Planner model configuration.
/// Deliberately not a general model-selection framework: one optional value,
/// flowed straight into the Planner's <see cref="AI.Factory.Domain.Entities.AgentRun.Model"/>
/// via <see cref="IProjectPlanner.GenerateAsync"/>. Null means "let the
/// runtime/provider pick its own default", same as every other AgentRun
/// created without an explicit Model today.
/// </summary>
public sealed class PlannerOptions
{
    public const string SectionName = "Planner";

    public string? Model { get; set; }
}
