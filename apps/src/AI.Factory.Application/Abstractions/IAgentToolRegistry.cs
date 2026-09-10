namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Discovers and executes <see cref="IAgentTool"/> instances by name.
/// Runtime-agnostic — no Hermes-specific behavior. Registration happens once
/// at composition time (DI); resolution/enumeration must be deterministic.
/// </summary>
public interface IAgentToolRegistry
{
    /// <summary>Descriptors for enabled tools only — a disabled tool is never exposed.</summary>
    IReadOnlyList<AgentToolDescriptor> GetDescriptors();

    /// <summary>
    /// Executes the named tool, measuring duration and translating any
    /// thrown exception into a failed <see cref="AgentToolResult"/> rather
    /// than propagating it. Unknown or disabled tool names fail the same way.
    /// </summary>
    Task<AgentToolResult> ExecuteAsync(
        string toolName,
        AgentToolContext context,
        CancellationToken cancellationToken = default);
}

public sealed record AgentToolDescriptor(
    string Name,
    string Description,
    string InputSchema,
    bool Enabled);
