using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Agents;

namespace AI.Factory.Infrastructure.Validation;

public sealed class DotNetBuildExecutor : IBuildExecutor
{
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
            var result = await _processRunner.RunAsync(
                new ProcessRunRequest("dotnet", new[] { "build", request.ProjectPath }, request.Timeout),
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
