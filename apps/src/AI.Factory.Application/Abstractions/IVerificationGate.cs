using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.8 — deterministic verification of a WorkItem's implementation.
/// Never trusts an agent's self-reported "completed" status: runs the
/// Factory's own trusted build/test commands (via <see cref="IValidationEngine"/>,
/// itself bounded by IProcessRunner's command allow-list) and reports a
/// deterministic outcome derived only from process exit signals.
/// </summary>
public interface IVerificationGate
{
    Task<VerificationResult> VerifyAsync(
        Project project,
        WorkItem workItem,
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
