using AI.Factory.Application.Agents;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.7 — converts an untrusted <see cref="StructuredAgentResult"/>
/// produced by a WorkItem-execution Agent into a deterministic execution
/// outcome. Pure interpretation only: it never persists WorkItems, creates
/// AgentRuns, schedules tasks, resolves dependencies, transitions Projects,
/// or invokes Hermes/an LLM — it just answers "what did the agent report,
/// and is that report structurally trustworthy?" (Phase 18.7 §6). Deciding
/// what a given outcome means for <see cref="AI.Factory.Domain.Enums.WorkItemStatus"/>
/// remains <see cref="AgentRunWorkItemSync"/>'s job.
/// </summary>
public interface IWorkItemExecutionResultMapper
{
    /// <summary>
    /// Throws <see cref="WorkItemExecutionResultInvalidException"/> if the
    /// structured result does not contain a well-formed execution result.
    /// </summary>
    WorkItemExecutionResult Map(StructuredAgentResult structuredResult);
}

/// <summary>
/// A WorkItem-execution Agent's structured output could not be interpreted
/// as a valid execution result — untrusted LLM output failed basic
/// shape/type validation (Phase 18.7 §12 "Execution result invalid").
/// </summary>
public sealed class WorkItemExecutionResultInvalidException : Exception
{
    public WorkItemExecutionResultInvalidException(string message) : base(message)
    {
    }
}

/// <summary>
/// Phase 18.7 §6/§12 — the only two outcomes an execution Agent may honestly
/// self-report; deliberately narrow (no arbitrary status string can reach
/// domain state). "RuntimeFailed" and "CandidateExecutionResultInvalid" are
/// not values here because they are never a successfully-parsed execution
/// result — they are decided one layer up, before or instead of calling
/// <see cref="IWorkItemExecutionResultMapper.Map"/> at all.
/// </summary>
public enum WorkItemExecutionResultOutcome
{
    /// <summary>The agent reports it genuinely finished the assigned WorkItem.</summary>
    ExecutionSucceeded,

    /// <summary>The agent reports it attempted the WorkItem but could not complete it — needs further work.</summary>
    ExecutionRequestedRepair
}

public sealed record WorkItemExecutionResult(WorkItemExecutionResultOutcome Outcome, string? Summary);
