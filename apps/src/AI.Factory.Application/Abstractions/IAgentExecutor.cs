using AI.Factory.Application.Abstractions.Execution;

namespace AI.Factory.Application.Abstractions;

public interface IAgentExecutor
{
    Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AgentExecutionRequest(
    string Agent,
    string? Model,
    string Prompt,
    string WorkingDirectory,
    Guid? AgentRunId = null,
    Guid? ProjectId = null,
    string? Adapter = null,
    TimeSpan? Timeout = null,
    DateTimeOffset? Deadline = null,
    string? Gateway = null);

/// <param name="ExitCode">The agent process exit code, when a process ran to completion; null otherwise.</param>
/// <param name="Usage">Token usage the runtime itself reported; null when it reported none (never zero-filled).</param>
public sealed record AgentExecutionResult(
    bool Success,
    string? Output,
    string? Error,
    long DurationMs,
    string? WorkspacePath = null,
    int? ExitCode = null,
    bool TimedOut = false,
    bool Cancelled = false,
    ExecutionUsage? Usage = null,
    ExecutionRuntimeReport? Report = null);
