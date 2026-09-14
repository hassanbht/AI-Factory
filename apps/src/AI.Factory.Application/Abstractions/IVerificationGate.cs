using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.8 — deterministic verification of a WorkItem's implementation.
/// Never trusts an agent's self-reported "completed" status: runs the
/// Factory's own trusted build/test commands (via IBuildExecutor/ITestExecutor,
/// itself bounded by IProcessRunner's command allow-list) and reports a
/// deterministic outcome derived only from process exit signals.
/// </summary>
public interface IVerificationGate
{
    Task<VerificationResult> VerifyAsync(
        Project project,
        WorkItem workItem,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 18.10 — the same deterministic build/test run as
    /// <see cref="VerifyAsync"/>, scoped to the whole Project rather than one
    /// WorkItem's attempt. No WorkItem context is needed: the underlying
    /// build/test executors already operate only on Project.RootPath.
    /// </summary>
    Task<VerificationResult> VerifyProjectAsync(
        Project project,
        CancellationToken cancellationToken = default);
}

public enum VerificationStatus
{
    Passed,
    Failed,
    InfrastructureError,
    TimedOut,
    Cancelled
}

/// <summary>
/// Summary is a bounded diagnostic string (never raw unlimited stdout/stderr).
/// </summary>
public sealed record VerificationResult(
    VerificationStatus Status,
    string Summary,
    int? ExitCode,
    TimeSpan Duration);
