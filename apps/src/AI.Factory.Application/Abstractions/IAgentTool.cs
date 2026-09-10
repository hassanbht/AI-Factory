namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Exposes one tool capability to an agent runtime. Runtime-agnostic: the
/// same tool must be usable by Hermes, Claude, Codex, or another runtime
/// without this contract changing. Discovery/enablement is handled by
/// <see cref="IAgentToolRegistry"/>, not by the tool itself calling back
/// into a runtime.
/// </summary>
public interface IAgentTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>Machine-readable JSON Schema describing the expected InputJson shape.</summary>
    string InputSchema { get; }

    bool Enabled { get; }

    Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context,
        CancellationToken cancellationToken = default);
}

public sealed record AgentToolContext(
    Guid AgentRunId,
    Guid? AgentDefinitionId,
    Guid? CorrelationId,
    string InputJson);

public sealed record AgentToolResult(
    bool Success,
    string? OutputJson,
    string? ErrorMessage,
    long DurationMs);
