using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Executes one <see cref="AgentRun"/>. Contract only in Phase 18.1.2 — no
/// implementation yet; a future HermesRuntime (or any other runtime) becomes
/// an Infrastructure adapter behind this interface without the Agent domain
/// model changing.
/// </summary>
public interface IAgentRuntime
{
    Task<AgentRunExecutionResult> ExecuteAsync(
        AgentRun agentRun,
        CancellationToken cancellationToken = default);
}

/// <summary>Stable result contract for any runtime executing an AgentRun. Large raw payloads belong in the Artifacts store, referenced here by id/path — not inlined.</summary>
public sealed record AgentRunExecutionResult(
    bool Success,
    string? Output,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    long DurationMs,
    IReadOnlyList<string> ArtifactReferences,
    IReadOnlyList<string> ToolCallNames,
    string? ErrorMessage,
    FailureCategory? FailureCategory,
    string? Provider,
    string? Model);
