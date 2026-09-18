using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.10 — deterministic Project-level completion/review gate:
/// Implementation -> Testing -> Reviewing -> HumanReview. Never invents a
/// second Project-wide repair loop (§9): a genuine Project-level build/test
/// failure fails the Project closed rather than looping — WorkItem-level
/// repair (Phase 18.9) is the Factory's only repair mechanism. Never
/// transitions a Project to Completed itself (§12) — HumanReview -> Completed
/// remains the existing explicit human/API action via ProjectService.
/// "Required WorkItem" (§5): no optional-task concept exists on WorkItem
/// today, so every WorkItem belonging to the Project is required. A "valid
/// plan" precondition (§5/§10) is not separately checked: nothing in the
/// existing ProjectTransitions/ProjectService lifecycle already requires one
/// for any transition, so requiring it only here would be an invented rule,
/// not a reused one — the empty-Project fail-closed check below already
/// covers the degenerate "no plan/no work" case.
/// </summary>
public sealed class ProjectCompletionGate : IProjectCompletionGate
{
    private readonly IProjectRepository _projects;
    private readonly IAgentRunRepository _agentRuns;
    private readonly IVerificationGate _verificationGate;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<ProjectCompletionGate> _logger;

    public ProjectCompletionGate(
        IProjectRepository projects,
        IAgentRunRepository agentRuns,
        IVerificationGate verificationGate,
        ILogger<ProjectCompletionGate> logger,
        IAuditLogger? auditLogger = null)
    {
        _projects = projects;
        _agentRuns = agentRuns;
        _verificationGate = verificationGate;
        // Phase 18.26 §3 — optional so every pre-existing caller that
        // constructs this gate directly keeps compiling; DI supplies the real
        // logger in both composition roots.
        _auditLogger = auditLogger ?? NullAuditLogger.Instance;
        _logger = logger;
    }

    public async Task<ProjectCompletionGateResult> EvaluateAsync(
        Project project, IReadOnlyList<WorkItem> workItems, CancellationToken cancellationToken = default)
    {
        return project.Status switch
        {
            ProjectStatus.Implementation => await TryAdvanceToTestingAsync(project, workItems, cancellationToken),
            ProjectStatus.Testing => await RunProjectVerificationAsync(project, cancellationToken),
            ProjectStatus.Reviewing => await TryAdvanceToHumanReviewAsync(project, workItems, cancellationToken),
            _ => new ProjectCompletionGateResult(ProjectCompletionGateAction.None, null)
        };
    }

    /// <summary>
    /// §4/§6/§15 — a Project becomes eligible for its own Testing gate only
    /// once every WorkItem it owns is Completed (the only status
    /// ITaskDependencyResolver ever treats as satisfying a dependency) and no
    /// AgentRun tied to the Project is still active. An empty Project
    /// (§15) is deliberately never eligible — fail-closed, not "vacuously done".
    /// </summary>
    private async Task<ProjectCompletionGateResult> TryAdvanceToTestingAsync(
        Project project, IReadOnlyList<WorkItem> workItems, CancellationToken cancellationToken)
    {
        if (!AllRequiredWorkItemsCompleted(workItems))
        {
            return new ProjectCompletionGateResult(ProjectCompletionGateAction.None, null);
        }

        if (await _agentRuns.HasActiveRunsForProjectAsync(project.Id, cancellationToken))
        {
            return new ProjectCompletionGateResult(
                ProjectCompletionGateAction.None, "Active AgentRuns remain for the project.");
        }

        project.CurrentError = null;

        if (!await TryTransitionAsync(project, ProjectStatus.Testing, cancellationToken))
        {
            return new ProjectCompletionGateResult(ProjectCompletionGateAction.None, null);
        }

        _logger.LogInformation("ProjectAdvancedToTesting ProjectId={ProjectId}", project.Id);
        return new ProjectCompletionGateResult(ProjectCompletionGateAction.AdvancedToTesting, null);
    }

    /// <summary>
    /// §8/§9 — Passed advances to Reviewing. A genuine Failed/InfrastructureError/
    /// TimedOut fails the Project closed (no Project-level Repairing loop is
    /// created — WorkItem-level repair, Phase 18.9, is the only repair path
    /// this Factory has). Cancelled propagates as Cancelled.
    /// </summary>
    private async Task<ProjectCompletionGateResult> RunProjectVerificationAsync(
        Project project, CancellationToken cancellationToken)
    {
        var result = await _verificationGate.VerifyProjectAsync(project, cancellationToken);

        _logger.LogInformation(
            "ProjectVerificationCompleted ProjectId={ProjectId} Status={Status} DurationMs={DurationMs}",
            project.Id, result.Status, result.Duration.TotalMilliseconds);

        // Phase 18.26 §3/§4 — the verification outcome becomes a durable audit
        // row, not just a log line. Only the Factory's own bounded summary is
        // carried (the same text Project.CurrentError already persists) — never
        // raw process or model output.
        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.ValidationResult,
            result.Status == VerificationStatus.Passed ? AuditSeverity.Info : AuditSeverity.Error,
            result.Status == VerificationStatus.Passed
                ? $"Project '{project.Id}' verification passed."
                : $"Project '{project.Id}' verification did not pass ({result.Status}): {result.Summary}",
            projectId: project.Id,
            outcome: result.Status.ToString(),
            actorType: "System",
            actorId: nameof(ProjectCompletionGate),
            durationMs: (long)result.Duration.TotalMilliseconds,
            cancellationToken: cancellationToken);

        var (target, message, action) = result.Status switch
        {
            VerificationStatus.Passed =>
                (ProjectStatus.Reviewing, (string?)null, ProjectCompletionGateAction.AdvancedToReviewing),
            VerificationStatus.Failed =>
                (ProjectStatus.Failed, result.Summary, ProjectCompletionGateAction.Failed),
            VerificationStatus.InfrastructureError =>
                (ProjectStatus.Failed, result.Summary, ProjectCompletionGateAction.Failed),
            VerificationStatus.TimedOut =>
                (ProjectStatus.Failed, result.Summary, ProjectCompletionGateAction.Failed),
            VerificationStatus.Cancelled =>
                (ProjectStatus.Cancelled, result.Summary, ProjectCompletionGateAction.Cancelled),
            _ => (ProjectStatus.Failed, "Unrecognized project verification outcome.", ProjectCompletionGateAction.Failed)
        };

        project.CurrentError = message;

        if (!await TryTransitionAsync(project, target, cancellationToken))
        {
            return new ProjectCompletionGateResult(ProjectCompletionGateAction.None, null);
        }

        return new ProjectCompletionGateResult(action, message);
    }

    /// <summary>
    /// §10/§11/§16 — the Reviewing gate re-confirms the same completion facts
    /// (defensive: nothing may have regressed since Testing) plus, per §16,
    /// that no AgentRun is still active. Only ever advances to HumanReview —
    /// never directly to Completed (§11/§12): that remains an explicit human
    /// action through the existing ProjectService/PATCH endpoint.
    /// </summary>
    private async Task<ProjectCompletionGateResult> TryAdvanceToHumanReviewAsync(
        Project project, IReadOnlyList<WorkItem> workItems, CancellationToken cancellationToken)
    {
        if (!AllRequiredWorkItemsCompleted(workItems))
        {
            return new ProjectCompletionGateResult(ProjectCompletionGateAction.None, null);
        }

        if (await _agentRuns.HasActiveRunsForProjectAsync(project.Id, cancellationToken))
        {
            return new ProjectCompletionGateResult(
                ProjectCompletionGateAction.None, "Active AgentRuns remain for the project.");
        }

        project.CurrentError = null;

        if (!await TryTransitionAsync(project, ProjectStatus.HumanReview, cancellationToken))
        {
            return new ProjectCompletionGateResult(ProjectCompletionGateAction.None, null);
        }

        _logger.LogInformation("ProjectAdvancedToHumanReview ProjectId={ProjectId}", project.Id);
        return new ProjectCompletionGateResult(ProjectCompletionGateAction.AdvancedToHumanReview, null);
    }

    private async Task<bool> TryTransitionAsync(
        Project project, ProjectStatus target, CancellationToken cancellationToken)
    {
        var from = project.Status;

        try
        {
            project.TransitionTo(target);
            await _projects.SaveChangesAsync(cancellationToken);

            // Phase 18.26 §3 — every gate-owned Project transition
            // (Testing/Reviewing/HumanReview, and the fail-closed
            // Failed/Cancelled outcomes) is now reconstructable from the
            // durable audit trail rather than from transient logs alone.
            var isFailure = target is ProjectStatus.Failed or ProjectStatus.Cancelled;

            await _auditLogger.LogAsync(
                Guid.NewGuid(),
                isFailure ? AuditEventType.WorkflowFailed : AuditEventType.WorkflowStateChanged,
                isFailure ? AuditSeverity.Error : AuditSeverity.Info,
                $"Project '{project.Id}' advanced from '{from}' to '{target}'."
                    + (isFailure && project.CurrentError is { Length: > 0 } ? $" {project.CurrentError}" : string.Empty),
                projectId: project.Id,
                outcome: target.ToString(),
                actorType: "System",
                actorId: nameof(ProjectCompletionGate),
                cancellationToken: cancellationToken);

            return true;
        }
        catch (ConcurrencyConflictException)
        {
            // Another writer (a concurrent scheduler cycle, or a human via
            // the manual status endpoint) already moved this Project —
            // losing this race is a safe no-op, never a retry/overwrite.
            _logger.LogInformation(
                "ProjectCompletionGateLostRace ProjectId={ProjectId} Target={Target}", project.Id, target);
            return false;
        }
    }

    private static bool AllRequiredWorkItemsCompleted(IReadOnlyList<WorkItem> workItems) =>
        workItems.Count > 0 && workItems.All(x => x.Status == WorkItemStatus.Completed.ToString());
}
