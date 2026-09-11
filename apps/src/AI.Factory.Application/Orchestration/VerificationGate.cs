using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.8 — the deterministic verification gate. Reuses the existing
/// trusted build/test executors (IBuildExecutor / ITestExecutor, themselves
/// bounded by IProcessRunner's command allow-list) rather than inventing a
/// second process-execution abstraction. IValidationEngine is not reused
/// here because its ValidationResult shape discards the TimedOut signal this
/// gate needs to tell "timed out" apart from "process could not be
/// launched" apart from a genuine non-zero-exit build/test failure — the
/// outcome is derived only from Success/ExitCode/TimedOut, never from
/// stdout/stderr text.
/// </summary>
public sealed class VerificationGate : IVerificationGate
{
    private readonly IBuildExecutor _buildExecutor;
    private readonly ITestExecutor _testExecutor;
    private readonly ExecutionLimitsOptions _executionLimits;
    private readonly ILogger<VerificationGate> _logger;

    public VerificationGate(
        IBuildExecutor buildExecutor,
        ITestExecutor testExecutor,
        ExecutionLimitsOptions executionLimits,
        ILogger<VerificationGate> logger)
    {
        _buildExecutor = buildExecutor;
        _testExecutor = testExecutor;
        _executionLimits = executionLimits;
        _logger = logger;
    }

    public async Task<VerificationResult> VerifyAsync(
        Project project, WorkItem workItem, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var build = await _buildExecutor.BuildAsync(
                new BuildRequest(project.RootPath, TimeSpan.FromMinutes(_executionLimits.BuildTimeoutMinutes)),
                cancellationToken);

            if (build.TimedOut)
            {
                return Result(VerificationStatus.TimedOut, "Build timed out.", build.ExitCode, stopwatch);
            }

            if (!build.Success)
            {
                return build.ExitCode == -1
                    ? Result(
                        VerificationStatus.InfrastructureError,
                        $"Build could not be executed: {Truncate(build.StandardError)}", build.ExitCode, stopwatch)
                    : Result(
                        VerificationStatus.Failed,
                        $"Build failed (exit code {build.ExitCode}): {Truncate(Tail(build.StandardOutput, build.StandardError))}",
                        build.ExitCode, stopwatch);
            }

            var test = await _testExecutor.TestAsync(
                new TestRequest(project.RootPath, TimeSpan.FromMinutes(_executionLimits.TestTimeoutMinutes)),
                cancellationToken);

            if (test.TimedOut)
            {
                return Result(VerificationStatus.TimedOut, "Tests timed out.", test.ExitCode, stopwatch);
            }

            if (!test.Success)
            {
                return test.ExitCode == -1
                    ? Result(
                        VerificationStatus.InfrastructureError,
                        $"Tests could not be executed: {Truncate(test.StandardError)}", test.ExitCode, stopwatch)
                    : Result(
                        VerificationStatus.Failed,
                        $"Tests failed (exit code {test.ExitCode}): {Truncate(Tail(test.StandardOutput, test.StandardError))}",
                        test.ExitCode, stopwatch);
            }

            return Result(VerificationStatus.Passed, "Build and tests passed.", test.ExitCode, stopwatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(VerificationStatus.Cancelled, "Verification was cancelled.", null, stopwatch);
        }
        catch (Exception ex)
        {
            // Anything else reaching here (e.g. a command-policy violation)
            // is an unexpected infrastructure failure rather than a
            // build/test outcome — IBuildExecutor/ITestExecutor already
            // convert their own known failure modes into a normal result.
            _logger.LogError(
                ex, "VerificationInfrastructureError WorkItemId={WorkItemId} ProjectId={ProjectId}",
                workItem.Id, project.Id);
            return Result(
                VerificationStatus.InfrastructureError, $"Verification infrastructure error: {ex.Message}",
                null, stopwatch);
        }
    }

    private static VerificationResult Result(
        VerificationStatus status, string summary, int? exitCode, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return new VerificationResult(status, summary, exitCode, stopwatch.Elapsed);
    }

    private static string Tail(string standardOutput, string standardError) =>
        string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;

    private static string Truncate(string value) => value.Length <= 2000 ? value : value[..2000] + " …(truncated)";
}
