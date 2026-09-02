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
    string WorkingDirectory);

public sealed record AgentExecutionResult(
    bool Success,
    string? Output,
    string? Error,
    long DurationMs,
    string? WorkspacePath = null);
