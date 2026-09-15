using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.14 — the one Factory-owned <see cref="IWorkspaceResolver"/>
/// implementation. Resolves Project.RootPath to an absolute, existing,
/// (optionally) boundary-checked workspace path. Reuses
/// <see cref="WorkspacePathGuard"/> for the boundary check rather than
/// reimplementing path-traversal/symlink-escape protection.
/// </summary>
public sealed class ProjectWorkspaceResolver : IWorkspaceResolver
{
    private readonly AgentWorkspaceOptions _options;

    public ProjectWorkspaceResolver(AgentWorkspaceOptions options)
    {
        _options = options;
    }

    public WorkspaceResolution Resolve(Project project)
    {
        var (isValid, path, failureReason) = WorkspacePathResolution.ResolveBoundedPath(project, _options);

        if (!isValid)
        {
            return WorkspaceResolution.Invalid(failureReason!);
        }

        if (!Directory.Exists(path))
        {
            return WorkspaceResolution.Invalid(
                $"Project '{project.Id}' workspace '{path}' does not exist or is not accessible.");
        }

        return WorkspaceResolution.Valid(path!);
    }
}

/// <summary>
/// Phase 18.15 — the non-existence-checking half of workspace path
/// resolution (null/empty check + <see cref="Path.GetFullPath(string)"/> +
/// optional <see cref="AgentWorkspaceOptions.AllowedRoot"/> boundary via
/// <see cref="WorkspacePathGuard"/>), factored out so
/// <see cref="ProjectWorkspaceResolver"/> and the Phase 18.15
/// ProjectWorkspaceProvisioner share one path-validity algorithm rather than
/// each independently deciding what a valid workspace path is (spec §10).
/// </summary>
internal static class WorkspacePathResolution
{
    public static (bool IsValid, string? Path, string? FailureReason) ResolveBoundedPath(
        Project project, AgentWorkspaceOptions options)
    {
        if (string.IsNullOrWhiteSpace(project.RootPath))
        {
            return (false, null, $"Project '{project.Id}' has no RootPath configured.");
        }

        string resolved;

        try
        {
            // Phase 18.16 §2 — when a fixed ProjectsBaseDirectory is
            // configured, a RELATIVE RootPath resolves against it instead of
            // the ambient process CWD; an already-absolute RootPath is
            // unaffected. Unset (default) reproduces the exact prior
            // CWD-relative Path.GetFullPath behavior.
            if (!string.IsNullOrWhiteSpace(options.ProjectsBaseDirectory) && !Path.IsPathRooted(project.RootPath))
            {
                var normalizedBase = Path.GetFullPath(options.ProjectsBaseDirectory);
                resolved = Path.GetFullPath(Path.Combine(normalizedBase, project.RootPath));
            }
            else
            {
                resolved = Path.GetFullPath(project.RootPath);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return (false, null, $"Project '{project.Id}' RootPath '{project.RootPath}' is malformed: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(options.AllowedRoot))
        {
            try
            {
                resolved = WorkspacePathGuard.EnsureWithinRoot(options.AllowedRoot, resolved);
            }
            catch (SecurityViolationException ex)
            {
                return (false, null, ex.Message);
            }
        }

        return (true, resolved, null);
    }
}
