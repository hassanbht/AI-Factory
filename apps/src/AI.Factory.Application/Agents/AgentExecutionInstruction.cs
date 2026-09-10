namespace AI.Factory.Application.Agents;

/// <summary>
/// The structured request the Factory hands to the instruction/prompt builder —
/// sits between AgentRun and the actual text sent to <see cref="Abstractions.IAgentRuntime"/>.
/// Fields are only what today's orchestration flow actually has to say (Phase
/// 18.1.8 §2); nothing speculative for a future Planner/Developer/Reviewer
/// role has been added here.
/// </summary>
public sealed record AgentExecutionInstruction(
    Guid CorrelationId,
    string Objective,
    IReadOnlyList<string> Instructions,
    string? ProjectContext,
    IReadOnlyList<string> Constraints,
    string ExpectedOutputFormat,
    IReadOnlyList<string> AllowedCapabilities,
    string? PreviousContext);
