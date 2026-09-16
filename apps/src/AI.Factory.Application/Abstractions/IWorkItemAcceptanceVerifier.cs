using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.21 §5 — evaluates a WorkItem's explicit, machine-checkable
/// acceptance criteria against the real Project workspace.
///
/// This exists because of the gap Phase 18.20 measured live: an agent that
/// does nothing at all still leaves an already-green test suite green, so
/// build/test verification alone accepted the WorkItem. Build/test answers
/// "is the project still healthy"; this answers the separate question "does
/// the outcome this WorkItem asked for actually exist in the workspace".
///
/// It never replaces <see cref="IVerificationGate"/> — the build/test gate
/// stays mandatory and runs first. It never executes agent-supplied commands,
/// and it never consults the agent's own report (§7): every criterion is
/// evaluated deterministically by the Factory against files on disk.
/// </summary>
public interface IWorkItemAcceptanceVerifier
{
    /// <summary>
    /// Evaluates <paramref name="workItem"/>'s acceptance criteria against
    /// <paramref name="workspacePath"/> (already resolved by the caller
    /// through <c>IWorkspaceResolver</c>, so agent and verifier always judge
    /// the same directory).
    /// </summary>
    AcceptanceVerificationResult Verify(WorkItem workItem, string workspacePath);
}

public enum AcceptanceVerificationStatus
{
    /// <summary>
    /// No machine-checkable contract exists for this WorkItem — either the
    /// field is empty or it holds legacy free-text criteria. Phase 18.21 §9:
    /// such WorkItems keep the historical build/test-only semantics, and this
    /// is deliberately reported as its own status so a report can never claim
    /// they were acceptance-verified.
    /// </summary>
    NotSpecified,

    /// <summary>Every declared criterion held against the real workspace.</summary>
    Passed,

    /// <summary>At least one criterion did not hold — a genuine, repairable outcome gap.</summary>
    Failed,

    /// <summary>
    /// The criteria themselves could not be evaluated (malformed envelope,
    /// unsupported criterion type, unsafe path, unreadable workspace). An
    /// operator/planner authoring error, never something a repair agent can
    /// fix, so it must not be routed into the repair loop.
    /// </summary>
    ContractInvalid
}

/// <param name="Status">The deterministic outcome.</param>
/// <param name="Summary">Bounded, human-readable evidence — the exact text handed to the repair agent on failure.</param>
/// <param name="EvaluatedCriteriaCount">How many criteria were actually evaluated (0 when not specified/invalid).</param>
public sealed record AcceptanceVerificationResult(
    AcceptanceVerificationStatus Status,
    string? Summary,
    int EvaluatedCriteriaCount);
