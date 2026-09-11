using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.5 — transforms an untrusted candidate plan into a validated,
/// normalized, atomically-persisted WorkItem DAG the existing
/// TaskDispatchScheduler can consume. Never executes anything, never calls
/// Hermes/IAgentRuntime — planning and task decomposition only.
/// </summary>
public interface IProjectPlanService
{
    /// <summary>
    /// Validates, normalizes, and persists <paramref name="request"/>'s
    /// candidate tasks as WorkItems + WorkItemDependencies for the target
    /// Project, atomically. Idempotent: an identical repeated request
    /// returns the existing plan rather than duplicating it; a
    /// structurally different request against a Project that already has
    /// a plan is rejected (see Phase 18.5 report §9).
    /// </summary>
    Task<ProjectPlanResult> CreatePlanAsync(
        ProjectPlanRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns the persisted plan for a Project, or null if none exists yet.</summary>
    Task<ProjectPlanResult?> GetPlanAsync(
        Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>One candidate task in an untrusted planning request — caller-supplied, not yet validated.</summary>
public sealed record PlannedWorkItemInput(
    string PlanKey,
    string Title,
    string? Description,
    WorkItemType Type,
    string Priority,
    IReadOnlyList<string> DependsOn);

public sealed record ProjectPlanRequest(
    Guid ProjectId,
    string? Summary,
    IReadOnlyList<PlannedWorkItemInput> Tasks);

public sealed record ProjectPlanTaskResult(
    string PlanKey,
    Guid WorkItemId,
    string Title,
    WorkItemType Type,
    string Priority,
    IReadOnlyList<string> DependsOn,
    string Status,
    TaskReadinessStatus Readiness);

/// <summary>
/// <paramref name="PlanHash"/> is the deterministic normalized-plan hash
/// (Phase 18.5 §9) — same candidate input always yields the same hash,
/// independent of persistence order/timestamps/GUIDs.
/// </summary>
public sealed record ProjectPlanResult(
    Guid ProjectId,
    string PlanHash,
    IReadOnlyList<ProjectPlanTaskResult> Tasks);
