using System.Runtime.CompilerServices;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Phase O — every WebApplicationFactory in this assembly hosts the real Api, whose project workspaces are created under
/// <c>AgentWorkspace:ProjectsBaseDirectory</c> (default: relative to the working directory, i.e. inside the test project).
/// Each test run therefore left <c>projects/projects/&lt;slug&gt;</c> directories in the repository — thousands of empty
/// ones and, since the Phase N commit, tracked nested <c>src-*</c> git repositories. The base directory (and the allowed root that
/// bounds it) now points at a per-process temp directory — an environment variable beats appsettings for every factory at
/// once — that is removed when the run ends.
/// </summary>
internal static class TestWorkspaceIsolation
{
    internal const string BaseDirectoryVariable = "AgentWorkspace__ProjectsBaseDirectory";
    internal const string AllowedRootVariable = "AgentWorkspace__AllowedRoot";

    [ModuleInitializer]
    internal static void Isolate()
    {
        var root = Path.Combine(Path.GetTempPath(), "aif-api-tests-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(BaseDirectoryVariable, root);
        Environment.SetEnvironmentVariable(AllowedRootVariable, root); // the Development overlay bounds workspaces to its own base

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file still held by a dying host; the OS temp cleaner owns what is left. Nothing lands in the repository.
            }
        };
    }
}
