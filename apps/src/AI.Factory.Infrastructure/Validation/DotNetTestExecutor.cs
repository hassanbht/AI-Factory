using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Agents;

namespace AI.Factory.Infrastructure.Validation;

public sealed class DotNetTestExecutor : ITestExecutor
{
    private readonly IProcessRunner _processRunner;

    public DotNetTestExecutor(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<TestExecutionResult> TestAsync(
        TestRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Same daemon-inherits-the-pipe hazard as the build path — see
            // DotNetBuildExecutor.NoDaemonFlags for the full explanation.
            // `dotnet test` drives the same MSBuild machinery, so it needs the
            // same flags; fixing only the build would leave half the gate exposed.
            // Same resolution as the build gate — the two must agree on what they verify.
            var target = MsBuildTargetResolver.Resolve(request.ProjectPath);
            var arguments = new List<string> { "test", target };
            arguments.AddRange(DotNetBuildExecutor.NoDaemonFlags);

            var result = await _processRunner.RunAsync(
                new ProcessRunRequest("dotnet", arguments, request.Timeout),
                cancellationToken);

            return new TestExecutionResult(
                !result.TimedOut && result.ExitCode == 0,
                result.StandardOutput,
                result.StandardError,
                result.TimedOut ? -1 : result.ExitCode,
                result.TimedOut);
        }
        catch (ProcessLaunchException ex)
        {
            return new TestExecutionResult(false, string.Empty, ex.Message, -1);
        }
    }
}
