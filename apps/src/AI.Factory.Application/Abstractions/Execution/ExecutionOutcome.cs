using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions.Execution;

/// <summary>
/// Terminal result of one execution. Exactly the four terminal outcomes the
/// existing AgentRun lifecycle already distinguishes — agent-reported
/// semantics such as "requested repair" stay in the structured result, which
/// the Factory (not the adapter) interprets.
/// </summary>
public enum ExecutionOutcomeStatus
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled
}

/// <summary>
/// Provider-neutral execution result. Everything a runtime may be unable to
/// report is nullable or empty rather than invented.
/// </summary>
/// <param name="FailureCategory">
/// The adapter's normalized classification (existing <see cref="Domain.Enums.FailureCategory"/>
/// taxonomy — no second classification system). Null on success.
/// </param>
/// <param name="ArtifactReferences">
/// Artifact ids/paths the runtime itself reports; large payloads are never inlined.
/// Workspace artifacts are recorded by the Factory after execution, not by adapters.
/// </param>
/// <param name="Model">The model/gateway the runtime reports it ran with.</param>
/// <param name="ExitReason">
/// Short machine-readable reason the execution ended, in the adapter's own terms
/// (for example <c>exit:0</c>, <c>exit:1</c>, <c>timeout</c>, <c>cancelled</c>).
/// Recorded as structured-result metadata; null when the adapter cannot tell.
/// </param>
public sealed record ExecutionOutcome(
    ExecutionOutcomeStatus Status,
    string? Output,
    string? ErrorMessage,
    FailureCategory? FailureCategory,
    long DurationMs,
    ExecutionUsage? Usage,
    IReadOnlyList<string> ArtifactReferences,
    IReadOnlyList<string> ToolCallNames,
    ModelRoute Model,
    string? ExitReason = null);

/// <summary>Token usage, when the runtime can report it. Every field is independently optional.</summary>
public sealed record ExecutionUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens);
