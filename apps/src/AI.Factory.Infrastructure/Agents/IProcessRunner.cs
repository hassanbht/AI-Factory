namespace AI.Factory.Infrastructure.Agents;

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

/// <summary>Thrown when the target executable could not be started at all (e.g. Docker is not installed).</summary>
public sealed class ProcessLaunchException : Exception
{
    public ProcessLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
