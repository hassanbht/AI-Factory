using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.3 §10 — the missing link between the generic AgentRun lifecycle
/// (which knows nothing about WorkItem — see AgentRun's own doc comment) and
/// WorkItem state. Called after an AgentRun reaches a terminal status for a
/// run that is linked to a WorkItem; maps that outcome onto WorkItem.Status
/// via the existing WorkItemTransitions state machine.
/// </summary>
public interface IAgentRunWorkItemSync
{
    Task SyncAsync(AgentRun agentRun, CancellationToken cancellationToken = default);
}
