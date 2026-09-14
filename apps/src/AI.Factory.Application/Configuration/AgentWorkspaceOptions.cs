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
}
