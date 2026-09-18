using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Planning;

/// <summary>
/// Phase 18.6 — the complete Planner workflow: create a Planner
/// <see cref="AgentRun"/> (executed by the existing AgentRunWorker/
/// AgentOrchestrator/IAgentRuntime pipeline, never invoked directly here),
/// then — once that AgentRun reaches a terminal result — map its structured
/// output into a candidate plan and hand it to the existing, authoritative
/// <see cref="IProjectPlanService"/>. Never calls Hermes/IAgentRuntime
/// itself and never persists WorkItems directly.
/// </summary>
public interface IProjectPlanner
{
    /// <summary>
    /// Validates the Project, resolves the enabled Planner AgentDefinition,
    /// and creates (or reuses an already in-flight) Planner AgentRun for it.
    /// Never executes synchronously — the returned AgentRun is Queued (or
    /// already Running/Waiting) for the existing AgentRunWorker to pick up.
    /// </summary>
    Task<AgentRun> GenerateAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called once a Planner AgentRun has reached a terminal status (normally
    /// by AgentRunWorker, right after AgentOrchestrator persists that
    /// terminal result). Idempotent: materializing an already-materialized
    /// run simply re-observes IProjectPlanService's own idempotent plan
    /// return, never duplicating WorkItems.
    /// </summary>
    Task<PlannerMaterializationResult> MaterializeAsync(Guid agentRunId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 18.25 — read-only query for the lifecycle owner: the newest
    /// Planner <see cref="AgentRun"/> belonging to a Project, or null if the
    /// Planner has never been dispatched for it. Exists so the lifecycle
    /// orchestrator can tell "planner not started yet" from "planner still
    /// running" from "planner already failed" without duplicating the
    /// Planner AgentDefinition lookup, and without ever retrying a Planner
    /// the Factory already ran (spec 18.25 §15 — bounded, fail-closed).
    /// </summary>
    Task<AgentRun?> GetLatestRunAsync(Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Phase 18.6 §15 — keeps LLM/runtime failure, candidate-mapping failure, and
/// Factory plan-validation failure as distinct, never-collapsed categories.
/// </summary>
public enum PlannerMaterializationOutcome
{
    /// <summary>Not a Planner run, not yet terminal, or otherwise not applicable — not an error.</summary>
    Skipped,

    /// <summary>The Planner AgentRun itself did not succeed (Failed/Cancelled/TimedOut).</summary>
    RuntimeFailed,

    /// <summary>The AgentRun succeeded, but its structured output could not be mapped into a candidate plan.</summary>
    CandidateInvalid,

    /// <summary>A well-formed candidate was mapped, but the Factory's deterministic plan validation rejected it.</summary>
    PlanValidationFailed,

    /// <summary>The candidate plan was validated, normalized, and persisted (or idempotently matched an existing plan).</summary>
    Succeeded
}

public sealed record PlannerMaterializationResult(
    PlannerMaterializationOutcome Outcome,
    ProjectPlanResult? Plan,
    string? Message)
{
    public static PlannerMaterializationResult Skip(string message) =>
        new(PlannerMaterializationOutcome.Skipped, null, message);
}
