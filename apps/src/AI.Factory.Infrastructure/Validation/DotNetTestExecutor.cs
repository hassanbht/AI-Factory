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
            var result = await _processRunner.RunAsync(
                new ProcessRunRequest("dotnet", new[] { "test", request.ProjectPath }, request.Timeout),
                cancellationToken);

            return new TestExecutionResult(
                !result.TimedOut && result.ExitCode == 0,
                result.StandardOutput,
                result.StandardError,
                result.TimedOut ? -1 : result.ExitCode);
        }
        catch (ProcessLaunchException ex)
        {
            return new TestExecutionResult(false, string.Empty, ex.Message, -1);
        }
    }
}
