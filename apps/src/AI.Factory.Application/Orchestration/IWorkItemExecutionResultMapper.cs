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
/// Phase 18.7 §6/§12 — the outcomes an execution Agent may honestly
/// self-report; deliberately narrow (no arbitrary status string can reach
/// domain state). "RuntimeFailed" is not a value here because it is never a
/// successfully-parsed execution result — it is decided one layer up, from
/// <see cref="AI.Factory.Domain.Enums.AgentRunStatus"/> itself, before
/// <see cref="IWorkItemExecutionResultMapper.Map"/> is called at all.
/// </summary>
public enum WorkItemExecutionResultOutcome
{
    /// <summary>The agent reports it genuinely finished the assigned WorkItem.</summary>
    ExecutionSucceeded,

    /// <summary>The agent reports it attempted the WorkItem but could not complete it — needs further work.</summary>
    ExecutionRequestedRepair,

    /// <summary>
    /// Phase 18.20 §7 — the AgentRun itself ended Succeeded, but carried no
    /// usable execution metadata (absent, unparseable, wrong version, or an
    /// unrecognized status value). Deliberately distinct from both
    /// <see cref="ExecutionRequestedRepair"/> (never inferred from silence —
    /// §4) and from a runtime failure: it asserts nothing about whether the
    /// work itself succeeded, only that the agent's *report* is unusable.
    /// Never returned by <see cref="IWorkItemExecutionResultMapper.Map"/>,
    /// which stays strict and throws instead (§6); it is produced at the
    /// interpretation boundary in <see cref="AgentRunWorkItemSync"/>, whose
    /// policy is to hand such a run to the Factory's own verification rather
    /// than dead-ending it.
    /// </summary>
    ExecutionMetadataUnavailable,

    /// <summary>
    /// Phase P — the AgentRun ended Succeeded at the runtime level (e.g. exit code 0), but the agent's
    /// own valid structured result explicitly reports Failed, TimedOut or Cancelled. That report is not
    /// "unusable metadata": it is the agent saying it did not do the work, so the WorkItem fails with the
    /// agent's own reason and never enters verification or acceptance. Like
    /// <see cref="ExecutionMetadataUnavailable"/>, produced only at the interpretation boundary in
    /// <see cref="AgentRunWorkItemSync"/>.
    /// </summary>
    ExecutionReportedFailure
}

/// <summary>Provider-neutral test outcome reported by an execution agent.</summary>
public sealed record ExecutionTestResult(string Name, string? Result);

/// <summary>
/// Interpreted execution report. RawExecutionReport preserves the exact JSON
/// execution object for audit/debug evidence; normalized collections avoid
/// coupling callers to a runtime-specific payload shape.
/// </summary>
public sealed record WorkItemExecutionResult(
    WorkItemExecutionResultOutcome Outcome,
    string? Summary,
    string? RawExecutionReport = null,
    IReadOnlyList<ExecutionTestResult>? Tests = null,
    IReadOnlyList<string>? Artifacts = null,
    IReadOnlyList<string>? Notes = null);
