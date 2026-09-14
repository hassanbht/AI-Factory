using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.14 — the single place that turns a Project's Factory-owned
/// RootPath into a trusted, absolute, execution-ready workspace path. Never
/// accepts a path from agent output, planner output, or API input — only
/// from the Project entity itself. Deterministic: the same Project always
/// resolves to the same workspace, with no dependency on process-local state,
/// so it is safe to call fresh after any Worker restart/retry/repair/recovery.
/// </summary>
public interface IWorkspaceResolver
{
    WorkspaceResolution Resolve(Project project);
}

public sealed record WorkspaceResolution(bool IsValid, string? Path, string? FailureReason)
{
    public static WorkspaceResolution Valid(string path) => new(true, path, null);

    public static WorkspaceResolution Invalid(string reason) => new(false, null, reason);
}
