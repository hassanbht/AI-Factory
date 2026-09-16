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
    private readonly IWorkspaceResolver _workspaceResolver;
    private readonly IWorkItemAcceptanceVerifier _acceptanceVerifier;
    private readonly ExecutionLimitsOptions _executionLimits;
    private readonly ILogger<VerificationGate> _logger;

    public VerificationGate(
        IBuildExecutor buildExecutor,
        ITestExecutor testExecutor,
        IWorkspaceResolver workspaceResolver,
        IWorkItemAcceptanceVerifier acceptanceVerifier,
        ExecutionLimitsOptions executionLimits,
        ILogger<VerificationGate> logger)
    {
        _buildExecutor = buildExecutor;
        _testExecutor = testExecutor;
        _workspaceResolver = workspaceResolver;
        _acceptanceVerifier = acceptanceVerifier;
        _executionLimits = executionLimits;
        _logger = logger;
    }

    public Task<VerificationResult> VerifyAsync(
        Project project, WorkItem workItem, CancellationToken cancellationToken = default) =>
        RunAsync(project, workItem, cancellationToken);

    /// <summary>Phase 18.10 — same build/test run, scoped to the whole Project (no WorkItem context needed).</summary>
    public Task<VerificationResult> VerifyProjectAsync(
        Project project, CancellationToken cancellationToken = default) =>
        RunAsync(project, null, cancellationToken);

    private async Task<VerificationResult> RunAsync(
        Project project, WorkItem? workItem, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Phase 18.16 §15 — resolve through the SAME IWorkspaceResolver
            // the Agent uses (Phase 18.14), rather than trusting the raw,
            // un-resolved project.RootPath. This makes "Agent workspace ==
            // Verification workspace" true by construction (shared
            // resolution), not by the two independently-CWD-dependent
            // resolutions happening to agree. No silent CWD fallback: a
            // resolution failure is a deterministic verification failure.
            var workspace = _workspaceResolver.Resolve(project);

            if (!workspace.IsValid)
            {
                return Result(
                    VerificationStatus.InfrastructureError,
                    $"Workspace could not be resolved for verification: {workspace.FailureReason}",
                    null, stopwatch);
            }

            var resolvedPath = workspace.Path!;

            var build = await _buildExecutor.BuildAsync(
                new BuildRequest(resolvedPath, TimeSpan.FromMinutes(_executionLimits.BuildTimeoutMinutes)),
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
                new TestRequest(resolvedPath, TimeSpan.FromMinutes(_executionLimits.TestTimeoutMinutes)),
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

            // Phase 18.21 §5/§10 — build/test proves the project is still
            // healthy; it cannot prove the WorkItem's own requested outcome
            // exists, because an agent that changed nothing leaves an
            // already-green suite green (the no-op acceptance gap measured
            // live in Phase 18.20). Explicit criteria are therefore evaluated
            // as a second, equally deterministic layer — never instead of the
            // build/test gate, and only for a WorkItem-scoped run.
            if (workItem is null)
            {
                return Result(VerificationStatus.Passed, "Build and tests passed.", test.ExitCode, stopwatch);
            }

            var acceptance = _acceptanceVerifier.Verify(workItem, resolvedPath);

            return acceptance.Status switch
            {
                // A real outcome gap: repairable, exactly like a failing test.
                AcceptanceVerificationStatus.Failed => Result(
                    VerificationStatus.Failed,
                    $"Build and tests passed, but acceptance criteria failed. {acceptance.Summary}",
                    test.ExitCode, stopwatch),

                // An authoring error in the criteria themselves — no agent can
                // repair that, so it must not enter the repair loop.
                AcceptanceVerificationStatus.ContractInvalid => Result(
                    VerificationStatus.InfrastructureError,
                    $"Acceptance criteria could not be evaluated: {acceptance.Summary}",
                    test.ExitCode, stopwatch),

                AcceptanceVerificationStatus.Passed => Result(
                    VerificationStatus.Passed,
                    $"Build and tests passed. {acceptance.Summary}",
                    test.ExitCode, stopwatch),

                // §9 — legacy WorkItem with no machine-checkable contract:
                // historical build/test-only semantics, stated explicitly so
                // it is never mistaken for acceptance verification.
                _ => Result(
                    VerificationStatus.Passed,
                    "Build and tests passed (no explicit acceptance criteria).",
                    test.ExitCode, stopwatch)
            };
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
                workItem?.Id, project.Id);
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
