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
        if (string.IsNullOrWhiteSpace(project.RootPath))
        {
            return WorkspaceResolution.Invalid($"Project '{project.Id}' has no RootPath configured.");
        }

        string resolved;

        try
        {
            resolved = Path.GetFullPath(project.RootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return WorkspaceResolution.Invalid(
                $"Project '{project.Id}' RootPath '{project.RootPath}' is malformed: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(_options.AllowedRoot))
        {
            try
            {
                resolved = WorkspacePathGuard.EnsureWithinRoot(_options.AllowedRoot, resolved);
            }
            catch (SecurityViolationException ex)
            {
                return WorkspaceResolution.Invalid(ex.Message);
            }
        }

        if (!Directory.Exists(resolved))
        {
            return WorkspaceResolution.Invalid(
                $"Project '{project.Id}' workspace '{resolved}' does not exist or is not accessible.");
        }

        return WorkspaceResolution.Valid(resolved);
    }
}
