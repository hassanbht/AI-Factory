namespace AI.Factory.Application.Agents;

/// <summary>
/// Turns a structured <see cref="AgentExecutionInstruction"/> into the actual
/// instruction text sent to the agent runtime — the one place this happens.
/// Callers (the orchestrator) must never concatenate prompt text themselves
/// (Phase 18.1.8 §6); this stays infrastructure for future specialized roles,
/// not those roles' own behavior.
/// </summary>
public interface IAgentInstructionBuilder
{
    string Build(AgentExecutionInstruction instruction);
}
