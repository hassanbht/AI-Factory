using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Planning;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.25 — the default <see cref="IProjectLifecycleOrchestrator"/>.
///
/// Deliberately thin. Every capability it needs already existed and is reused
/// unchanged: <see cref="IProjectWorkspaceProvisioner"/> for the workspace
/// prerequisite, <see cref="IProjectPlanner"/> for Planner dispatch and
/// materialization, <see cref="IProjectPlanService"/> for the authoritative
/// "is there a plan" read, <see cref="Project.TransitionTo"/> for validated
/// transitions, the Project row's own optimistic-concurrency token for
/// idempotency, and <see cref="IAuditLogger"/> for observability. It adds no
/// state store, no second transition table, and no retry loop of its own.
/// </summary>
public sealed class ProjectLifecycleOrchestrator : IProjectLifecycleOrchestrator
{
    /// <summary>
    /// The deterministic pre-planning chain. These stages have no executing
    /// agent in the current Factory (no Analysis/Specification/Architecture
    /// AgentDefinition exists), so the lifecycle owner walks them as validated
    /// no-work transitions rather than inventing agents for them. The chain is
    /// explicit rather than computed, so no stage can ever be skipped silently
    /// (spec 18.25 §6).
    /// </summary>
    private static readonly IReadOnlyDictionary<ProjectStatus, ProjectStatus> PrePlanningChain =
        new Dictionary<ProjectStatus, ProjectStatus>
        {
            [ProjectStatus.Analyzing] = ProjectStatus.Specification,
            [ProjectStatus.Specification] = ProjectStatus.Architecture,
            [ProjectStatus.Architecture] = ProjectStatus.Planning
        };

    private readonly IProjectRepository _projects;
    private readonly IProjectWorkspaceProvisioner _workspaceProvisioner;
    private readonly IProjectPlanner _planner;
    private readonly IProjectPlanService _planService;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<ProjectLifecycleOrchestrator> _logger;

    public ProjectLifecycleOrchestrator(
        IProjectRepository projects,
        IProjectWorkspaceProvisioner workspaceProvisioner,
        IProjectPlanner planner,
        IProjectPlanService planService,
        IAuditLogger auditLogger,
        ILogger<ProjectLifecycleOrchestrator> logger)
    {
        _projects = projects;
        _workspaceProvisioner = workspaceProvisioner;
        _planner = planner;
        _planService = planService;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    // ---------------- Start (spec §3/§4/§7/§11) ----------------

    public async Task<Project> StartAsync(
        Guid projectId, string? actor = null, CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException($"Project '{projectId}' was not found.");

        // §7 — idempotent by observation of the persisted marker, before any
        // other check: re-starting an already-started Project (whatever stage
        // it has since reached) is a safe no-op that creates no second
        // lifecycle execution, no second Planner run, and no duplicate
        // transition.
        if (project.LifecycleStartedAtUtc is not null)
        {
            _logger.LogInformation(
                "ProjectLifecycleStartAlreadyStarted ProjectId={ProjectId} Status={Status} StartedAtUtc={StartedAtUtc}",
                project.Id, project.Status, project.LifecycleStartedAtUtc);
            return project;
        }

        // §3 — the start condition is explicit, never inferred from the row's
        // existence. Analyzing is precisely "a source decision was made and
        // succeeded" (ProjectService.InitializeSourceAsync is the only thing
        // that produces it), which is why it, and only it, is startable.
        if (project.Status == ProjectStatus.Created)
        {
            throw new ConflictException(
                $"Project '{projectId}' has no initialized source. Initialize the project source before starting " +
                "autonomous execution.");
        }

        if (project.Status != ProjectStatus.Analyzing)
        {
            throw new ConflictException(
                $"Project '{projectId}' cannot start autonomous execution from status '{project.Status}'. " +
                $"Only a Project in '{ProjectStatus.Analyzing}' that has not already been started may be started.");
        }

        if (string.IsNullOrWhiteSpace(project.Description))
        {
            throw new ValidationException(
                $"Project '{projectId}' has no description/goal, so the Planner would have nothing to plan against.");
        }

        if (string.IsNullOrWhiteSpace(project.RootPath))
        {
            throw new ValidationException($"Project '{projectId}' has no RootPath.");
        }

        // §3/§10 — workspace prerequisite, checked through the existing
        // idempotent provisioner rather than a second filesystem check. Source
        // initialization itself is NOT re-performed here: that decision is
        // already final by the time a Project is Analyzing.
        var provisioning = _workspaceProvisioner.Provision(project);

        if (!provisioning.Success)
        {
            throw new IOException(
                $"Project '{projectId}' workspace is unavailable: {provisioning.FailureReason}");
        }

        project.LifecycleStartedAtUtc = DateTime.UtcNow;
        project.UpdatedAtUtc = DateTime.UtcNow;

        try
        {
            await _projects.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // §7 — two concurrent starts: exactly one wins the Version race.
            // The loser observes the winner's persisted state and returns it,
            // rather than surfacing a conflict for a request that, in effect,
            // already succeeded.
            var reloaded = await _projects.GetByIdAsync(projectId, cancellationToken);

            if (reloaded?.LifecycleStartedAtUtc is not null)
            {
                _logger.LogInformation(
                    "ProjectLifecycleStartLostRace ProjectId={ProjectId} Status={Status}", projectId, reloaded.Status);
                return reloaded;
            }

            throw;
        }

        _logger.LogInformation(
            "ProjectLifecycleStarted ProjectId={ProjectId} Status={Status}", project.Id, project.Status);

        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.WorkflowStarted,
            AuditSeverity.Info,
            $"Autonomous lifecycle started for project '{projectId}'.",
            projectId: projectId,
            outcome: project.Status.ToString(),
            actorType: actor is null ? "System" : "Human",
            actorId: actor,
            cancellationToken: cancellationToken);

        return project;
    }

    // ---------------- Advance (spec §5/§6/§8/§15) ----------------

    public async Task<ProjectLifecycleAdvanceResult> AdvanceAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetByIdAsync(projectId, cancellationToken);

        if (project is null)
        {
            return ProjectLifecycleAdvanceResult.None(ProjectStatus.Created, "Project not found.");
        }

        if (project.LifecycleStartedAtUtc is null)
        {
            // Never hijacks an operator-driven Project (§7).
            return ProjectLifecycleAdvanceResult.None(project.Status, "Autonomous lifecycle was never started.");
        }

        if (ProjectTransitions.IsTerminal(project.Status))
        {
            return ProjectLifecycleAdvanceResult.None(project.Status, "Project is terminal.");
        }

        if (PrePlanningChain.TryGetValue(project.Status, out var next))
        {
            return await TransitionAsync(project, next, cancellationToken);
        }

        if (project.Status == ProjectStatus.Planning)
        {
            return await AdvancePlanningAsync(project, cancellationToken);
        }

        // Implementation -> Testing -> Reviewing -> HumanReview belongs to
        // IProjectCompletionGate; HumanReview -> Completed belongs to the
        // human. This orchestrator deliberately owns neither (§5/§13).
        return ProjectLifecycleAdvanceResult.None(
            project.Status, "Stage is owned by the completion gate or by explicit human approval.");
    }

    /// <summary>
    /// §9/§15 — the Planner boundary. The Planner is dispatched through its
    /// normal production path exactly once per Project, its output is never
    /// rewritten, and a Planner that produced no usable plan fails the Project
    /// closed instead of letting it drift into Implementation with no work.
    /// </summary>
    private async Task<ProjectLifecycleAdvanceResult> AdvancePlanningAsync(
        Project project, CancellationToken cancellationToken)
    {
        var plan = await _planService.GetPlanAsync(project.Id, cancellationToken);

        if (plan is not null)
        {
            return await TransitionAsync(project, ProjectStatus.Implementation, cancellationToken);
        }

        var plannerRun = await _planner.GetLatestRunAsync(project.Id, cancellationToken);

        if (plannerRun is null)
        {
            var created = await _planner.GenerateAsync(project.Id, cancellationToken);

            _logger.LogInformation(
                "ProjectLifecyclePlannerStarted ProjectId={ProjectId} AgentRunId={AgentRunId}", project.Id, created.Id);

            await _auditLogger.LogAsync(
                Guid.NewGuid(),
                AuditEventType.AgentStarted,
                AuditSeverity.Info,
                $"Planner dispatched for project '{project.Id}'.",
                projectId: project.Id,
                runId: created.Id,
                outcome: ProjectStatus.Planning.ToString(),
                actorType: "System",
                actorId: nameof(ProjectLifecycleOrchestrator),
                cancellationToken: cancellationToken);

            return new ProjectLifecycleAdvanceResult(
                ProjectLifecycleAction.PlannerStarted, project.Status, $"Planner AgentRun '{created.Id}' dispatched.");
        }

        if (!AgentRunTransitions.IsTerminal(plannerRun.Status))
        {
            return new ProjectLifecycleAdvanceResult(
                ProjectLifecycleAction.Waiting, project.Status,
                $"Planner AgentRun '{plannerRun.Id}' is '{plannerRun.Status}'.");
        }

        if (plannerRun.Status != AgentRunStatus.Succeeded)
        {
            return await FailClosedAsync(
                project,
                $"Planner AgentRun '{plannerRun.Id}' ended in status '{plannerRun.Status}'.",
                cancellationToken);
        }

        // A terminal, Succeeded Planner run with no persisted plan means
        // materialization has not landed yet (AgentRunWorker normally does it
        // in the same cycle) or never landed at all (Worker crashed in that
        // window — §8). MaterializeAsync is idempotent by contract, so
        // re-driving it here is both the race resolution and the crash
        // recovery, with no second materialization path.
        var materialization = await _planner.MaterializeAsync(plannerRun.Id, cancellationToken);

        return materialization.Outcome switch
        {
            PlannerMaterializationOutcome.Succeeded =>
                await TransitionAsync(project, ProjectStatus.Implementation, cancellationToken),

            PlannerMaterializationOutcome.Skipped => new ProjectLifecycleAdvanceResult(
                ProjectLifecycleAction.Waiting, project.Status, materialization.Message),

            _ => await FailClosedAsync(
                project,
                $"Planner produced no usable plan ({materialization.Outcome}): {materialization.Message}",
                cancellationToken)
        };
    }

    /// <summary>
    /// §6 — every automatic transition goes through the existing validated
    /// <see cref="Project.TransitionTo"/> table; the orchestrator never
    /// assigns <see cref="Project.Status"/>. Losing a concurrency race is a
    /// safe no-op, exactly as in <see cref="ProjectCompletionGate"/>.
    /// </summary>
    private async Task<ProjectLifecycleAdvanceResult> TransitionAsync(
        Project project, ProjectStatus target, CancellationToken cancellationToken)
    {
        var from = project.Status;

        try
        {
            project.TransitionTo(target);
            await _projects.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            _logger.LogInformation(
                "ProjectLifecycleLostRace ProjectId={ProjectId} From={From} Target={Target}",
                project.Id, from, target);

            return ProjectLifecycleAdvanceResult.None(from, "Another writer advanced this project first.");
        }

        _logger.LogInformation(
            "ProjectLifecycleAdvanced ProjectId={ProjectId} From={From} To={To}", project.Id, from, target);

        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.WorkflowStateChanged,
            AuditSeverity.Info,
            $"Project '{project.Id}' advanced from '{from}' to '{target}'.",
            projectId: project.Id,
            outcome: target.ToString(),
            actorType: "System",
            actorId: nameof(ProjectLifecycleOrchestrator),
            cancellationToken: cancellationToken);

        return new ProjectLifecycleAdvanceResult(ProjectLifecycleAction.Advanced, target, null);
    }

    /// <summary>
    /// §15 — a lifecycle failure is a persisted, observable, terminal outcome.
    /// It never converts into an unrelated status to keep a happy path moving,
    /// and never leaves the Project stuck without a diagnostic.
    /// </summary>
    private async Task<ProjectLifecycleAdvanceResult> FailClosedAsync(
        Project project, string reason, CancellationToken cancellationToken)
    {
        project.CurrentError = reason;

        var from = project.Status;

        try
        {
            project.TransitionTo(ProjectStatus.Failed);
            await _projects.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            _logger.LogInformation(
                "ProjectLifecycleLostRace ProjectId={ProjectId} From={From} Target={Target}",
                project.Id, from, ProjectStatus.Failed);

            return ProjectLifecycleAdvanceResult.None(from, "Another writer advanced this project first.");
        }

        _logger.LogWarning(
            "ProjectLifecycleFailed ProjectId={ProjectId} From={From} Reason={Reason}", project.Id, from, reason);

        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.WorkflowFailed,
            AuditSeverity.Error,
            $"Project '{project.Id}' lifecycle failed in '{from}': {reason}",
            projectId: project.Id,
            outcome: ProjectStatus.Failed.ToString(),
            actorType: "System",
            actorId: nameof(ProjectLifecycleOrchestrator),
            cancellationToken: cancellationToken);

        return new ProjectLifecycleAdvanceResult(ProjectLifecycleAction.Failed, ProjectStatus.Failed, reason);
    }
}
