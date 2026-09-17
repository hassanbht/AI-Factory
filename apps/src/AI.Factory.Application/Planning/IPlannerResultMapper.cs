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

/// <summary>
/// Phase 18.23 §6 — the stage of the Planner pipeline a run reached before it
/// stopped. Recorded so a failed Planner run says WHERE it failed rather than
/// only that it failed; deliberately a small closed enum rather than free text,
/// so diagnostics stay aggregatable and carry no model output of their own.
/// </summary>
public enum PlannerDiagnosticStage
{
    /// <summary>Hermes/provider/runtime never produced a successful AgentRun.</summary>
    PlannerExecution,

    /// <summary>The AgentRun succeeded but carried no raw output to read.</summary>
    PlannerRawOutputReceived,

    /// <summary>No valid structured-result envelope could be read out of the raw output.</summary>
    PlannerStructuredParse,

    /// <summary>The envelope was valid but the candidate plan's shape/types were not.</summary>
    PlannerSchemaValidation,

    /// <summary>The plan was well-shaped but semantically invalid (keys, dependencies, priorities, cycles).</summary>
    PlannerPlanValidation,

    /// <summary>A task's acceptance contract was missing or unusable.</summary>
    PlannerAcceptanceValidation,

    /// <summary>The plan was accepted but could not be persisted.</summary>
    PlannerPersistence
}

/// <summary>A Planner Agent's structured output could not be mapped into a candidate plan — untrusted LLM output failed basic shape/type validation.</summary>
public sealed class PlannerCandidatePlanException : Exception
{
    public PlannerCandidatePlanException(string message)
        : this(message, PlannerDiagnosticStage.PlannerSchemaValidation)
    {
    }

    public PlannerCandidatePlanException(string message, PlannerDiagnosticStage stage) : base(message) =>
        Stage = stage;

    /// <summary>Which §6 stage rejected the candidate — schema shape, or the acceptance contract inside it.</summary>
    public PlannerDiagnosticStage Stage { get; }
}
