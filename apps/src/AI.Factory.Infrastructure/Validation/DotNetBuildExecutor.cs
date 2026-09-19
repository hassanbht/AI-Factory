using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Agents;

namespace AI.Factory.Infrastructure.Validation;

public sealed class DotNetBuildExecutor : IBuildExecutor
{
    /// <summary>
    /// Phase 18.23 — what keeps a verification build from hanging until its
    /// timeout on a machine whose MSBuild daemons are not already running.
    ///
    /// <see cref="IProcessRunner"/> redirects stdout/stderr and waits for the
    /// child's streams to reach EOF. By default <c>dotnet build</c> leaves
    /// persistent MSBuild worker nodes (and a VBCSCompiler server) alive so
    /// later builds start faster — and those daemons inherit the redirected
    /// stdout handle. The build itself finishes, but the pipe never closes, so
    /// the wait blocks until the build timeout and a perfectly good build is
    /// reported as TimedOut.
    ///
    /// Observed live: a verification gate reported
    /// <c>Status=TimedOut DurationMs=300019</c> while the same build, run by
    /// hand in the same workspace, took 3 seconds — and the daemons it spawned
    /// were still alive afterwards with start times matching the gate's build.
    /// It only reproduces when the gate is the FIRST thing to build after a
    /// reboot; once the daemons exist, later builds reuse them and inherit
    /// nothing, which is why it stayed hidden until now.
    ///
    /// Raising the timeout would only have hidden it (§14). These flags cost a
    /// little build speed and nothing else.
    /// </summary>
    internal static readonly string[] NoDaemonFlags =
    {
        "-nodeReuse:false",
        "-p:UseSharedCompilation=false"
    };

    private readonly IProcessRunner _processRunner;

    public DotNetBuildExecutor(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<BuildResult> BuildAsync(
        BuildRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // See MsBuildTargetResolver: unchanged when the workspace root already
            // holds a project/solution; resolves an unambiguous nested one otherwise.
            var target = MsBuildTargetResolver.Resolve(request.ProjectPath);
            var arguments = new List<string> { "build", target };
            arguments.AddRange(NoDaemonFlags);

            var result = await _processRunner.RunAsync(
                new ProcessRunRequest("dotnet", arguments, request.Timeout),
                cancellationToken);

            return new BuildResult(
                !result.TimedOut && result.ExitCode == 0,
                result.StandardOutput,
                result.StandardError,
                result.TimedOut ? -1 : result.ExitCode,
                result.TimedOut);
        }
        catch (ProcessLaunchException ex)
        {
            return new BuildResult(false, string.Empty, ex.Message, -1);
        }
    }
}
