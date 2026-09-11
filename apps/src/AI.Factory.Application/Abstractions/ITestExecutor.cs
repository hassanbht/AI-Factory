namespace AI.Factory.Application.Abstractions;

public interface ITestExecutor
{
    Task<TestExecutionResult> TestAsync(
        TestRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TestRequest(
    string ProjectPath,
    TimeSpan Timeout);

public sealed record TestExecutionResult(
    bool Success,
    string StandardOutput,
    string StandardError,
    int ExitCode,
    bool TimedOut = false);
