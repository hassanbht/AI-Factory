namespace AI.Factory.Application.Configuration;

/// <summary>
/// Phase 18.14 — optional defense-in-depth boundary for Agent workspace
/// resolution. Unset (default) preserves today's implicit trust model for
/// Project.RootPath (same as DotNetBuildExecutor/DotNetTestExecutor already
/// have): existence-checked only, no boundary. When set, WorkspacePathGuard
/// enforces that every resolved Project workspace stays inside this root.
/// </summary>
public sealed class AgentWorkspaceOptions
{
    public string? AllowedRoot { get; set; }

    /// <summary>
    /// Phase 18.16 §2/§8 — optional fixed, Factory-owned base directory that
    /// every RELATIVE Project.RootPath resolves against, instead of the
    /// ambient process current working directory. Unset (default) preserves
    /// the prior exact CWD-relative behavior. Never bound from API input or
    /// agent/LLM output — configuration only. An already-rooted (absolute)
    /// RootPath is unaffected either way.
    /// </summary>
    public string? ProjectsBaseDirectory { get; set; }

    /// <summary>Fail fast at startup on a malformed configured value, rather than surfacing a confusing failure mid-request.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectsBaseDirectory))
        {
            return;
        }

        try
        {
            Path.GetFullPath(ProjectsBaseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Invalid {nameof(AgentWorkspaceOptions)}: {nameof(ProjectsBaseDirectory)} '{ProjectsBaseDirectory}' is malformed: {ex.Message}");
        }
    }
}
