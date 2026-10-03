namespace AI.Factory.Infrastructure.Agents;

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default);
}

/// <param name="StandardInput">
/// Phase M — text written to the child's stdin, which is then closed. Stdin is always redirected and
/// closed (empty when null): a headless Worker must never hand a child its own stdin, on which a CLI
/// such as `codex exec` would otherwise wait. Also keeps large prompts off the command line.
/// </param>
/// <param name="OnStarted">Phase M — receives the started process (id, start time) so a caller can record it for crash reaping.</param>
/// <param name="OnStandardOutputLine">Phase M — observes each stdout line as it arrives (structured event streams).</param>
public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    string? StandardInput = null,
    Action<System.Diagnostics.Process>? OnStarted = null,
    Action<string>? OnStandardOutputLine = null);

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
