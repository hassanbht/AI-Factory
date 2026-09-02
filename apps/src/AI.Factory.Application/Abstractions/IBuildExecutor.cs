namespace AI.Factory.Application.Abstractions;

public interface IBuildExecutor
{
    Task<BuildResult> BuildAsync(
        BuildRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record BuildRequest(
    string ProjectPath,
    TimeSpan Timeout);

public sealed record BuildResult(
    bool Success,
    string StandardOutput,
    string StandardError,
    int ExitCode);
