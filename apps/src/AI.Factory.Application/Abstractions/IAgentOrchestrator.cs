using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Coordinates execution of one <see cref="Domain.Entities.AgentRun"/>: loads
/// it and its AgentDefinition, resolves available tools through
/// <see cref="IAgentToolRegistry"/> and contextual memory through
/// <see cref="IAgentMemory"/>, invokes the single runtime boundary
/// (<see cref="IAgentRuntime"/>), and persists the resulting lifecycle
/// transition. Never spawns processes, invokes Docker/Hermes directly, or
/// implements tool/memory-provider-specific behavior itself — all of that
/// stays behind the interfaces it depends on.
/// </summary>
public interface IAgentOrchestrator
{
    Task<AgentOrchestrationResult> ExecuteAsync(Guid agentRunId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <paramref name="Executed"/> distinguishes "the runtime actually ran" from
/// "execution was skipped" (not found, already terminal, already running,
/// invalid starting state, or lost a concurrent claim race) — in the skipped
/// case <paramref name="FinalStatus"/> reflects the AgentRun's unchanged
/// status, not a new transition.
/// </summary>
public sealed record AgentOrchestrationResult(
    bool Executed,
    bool Success,
    AgentRunStatus FinalStatus,
    string? Output,
    long DurationMs,
    IReadOnlyList<string> ToolCallNames,
    IReadOnlyList<string> AvailableToolNames,
    string? ErrorMessage,
    FailureCategory? FailureCategory,
    Guid CorrelationId);
