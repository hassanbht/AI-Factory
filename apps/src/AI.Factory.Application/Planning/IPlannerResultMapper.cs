using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;

namespace AI.Factory.Application.Planning;

/// <summary>
/// Phase 18.6 — converts an untrusted <see cref="StructuredAgentResult"/>
/// produced by the Planner Agent into a <see cref="ProjectPlanRequest"/>.
/// Structural/type validation only (missing fields, wrong JSON shapes,
/// unparseable <see cref="AI.Factory.Domain.Enums.WorkItemType"/> strings) —
/// never persists WorkItems, checks cycles, computes readiness, creates
/// AgentRuns, or schedules anything. Semantic plan validation (duplicate
/// keys, unknown dependency targets, priority values, AgentDefinition
/// compatibility, cycles) remains exclusively <see cref="IProjectPlanService"/>'s
/// job (Phase 18.6 §9/§17) — not duplicated here.
/// </summary>
public interface IPlannerResultMapper
{
    /// <summary>
    /// Throws <see cref="PlannerCandidatePlanException"/> if the structured
    /// result does not contain a well-formed candidate plan.
    /// </summary>
    ProjectPlanRequest Map(Guid projectId, StructuredAgentResult structuredResult);
}

/// <summary>A Planner Agent's structured output could not be mapped into a candidate plan — untrusted LLM output failed basic shape/type validation.</summary>
public sealed class PlannerCandidatePlanException : Exception
{
    public PlannerCandidatePlanException(string message) : base(message)
    {
    }
}
