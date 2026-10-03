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
/// <param name="GatewayFailure">
/// Phase J — the model-gateway failure reason behind a failed execution, when the runtime
/// reported one explicitly: a <c>ModelGatewayFailureReason</c> name (e.g. HTTP 402 -> "Billing"),
/// kept as a plain string so this contract stays BCL/Domain-only. Null when not applicable or
/// not available; never inferred. <see cref="FailureCategory"/> is unchanged by it.
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
    string? ExitReason = null,
    string? GatewayFailure = null,
    ExecutionRuntimeReport? Report = null);

/// <summary>Token usage, when the runtime can report it. Every field is independently optional.</summary>
public sealed record ExecutionUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens);

/// <summary>
/// Phase M — what the runtime itself reported about one execution, beyond tokens. Every field is
/// copied from the runtime's own structured output when — and only when — it was there: null means
/// "not exposed by this runtime/version", never zero, never inferred. The Factory persists it as
/// non-secret AgentRun metadata; it carries no credential and decides nothing.
/// </summary>
/// <param name="Runtime">The adapter key that produced it.</param>
/// <param name="RuntimeVersion">The CLI/runtime version string, when known.</param>
/// <param name="SessionId">The runtime's own session/thread id (non-secret).</param>
/// <param name="CostUsd">Cost the runtime reported; see <paramref name="CostSource"/> for what it is.</param>
/// <param name="CostSource">Which runtime field the cost came from, in the runtime's own terms (e.g. "&lt;runtime&gt;:total_cost_usd").</param>
/// <param name="UpstreamRequestId">A provider request id the runtime printed explicitly.</param>
/// <param name="Turns">Agent turns/API calls the runtime reported.</param>
/// <param name="ReportedModel">The model the runtime says it used (may differ in form from the routed id).</param>
public sealed record ExecutionRuntimeReport(
    string Runtime,
    string? RuntimeVersion = null,
    string? SessionId = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    int? ExitCode = null,
    decimal? CostUsd = null,
    string? CostSource = null,
    string? UpstreamRequestId = null,
    int? Turns = null,
    string? ReportedModel = null);
