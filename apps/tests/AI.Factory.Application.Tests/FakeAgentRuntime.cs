using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Deterministic IAgentRuntime test double — no Hermes, no Docker, no
/// network. Lets orchestration/worker tests (this phase and later ones)
/// exercise Success/Failure/Timeout/Cancellation outcomes without any
/// external LLM/agent dependency, per Phase 18.1.3 requirement that normal
/// CI never needs Hermes/OpenRouter/network access.
/// </summary>
public sealed class FakeAgentRuntime : IAgentRuntime
{
    public enum Mode
    {
        Success,
        Failure,
        Timeout,
        Cancellation
    }

    private readonly Mode _mode;
    private readonly string? _successOutput;

    public FakeAgentRuntime(Mode mode, string? successOutput = null)
    {
        _mode = mode;
        _successOutput = successOutput;
    }

    public int CallCount { get; private set; }

    public AgentRun? LastAgentRun { get; private set; }

    public Task<AgentRunExecutionResult> ExecuteAsync(
        AgentRun agentRun,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastAgentRun = agentRun;

        return _mode switch
        {
            Mode.Success => Task.FromResult(new AgentRunExecutionResult(
                Success: true,
                Output: _successOutput ?? "fake output",
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                DurationMs: 10,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                ErrorMessage: null,
                FailureCategory: null,
                Provider: agentRun.Provider,
                Model: agentRun.Model)),

            Mode.Failure => Task.FromResult(new AgentRunExecutionResult(
                Success: false,
                Output: null,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                DurationMs: 10,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                ErrorMessage: "Simulated agent failure.",
                FailureCategory: FailureCategory.Agent,
                Provider: agentRun.Provider,
                Model: agentRun.Model)),

            Mode.Timeout => Task.FromResult(new AgentRunExecutionResult(
                Success: false,
                Output: null,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                DurationMs: 10,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                ErrorMessage: "Simulated timeout.",
                FailureCategory: FailureCategory.Timeout,
                Provider: agentRun.Provider,
                Model: agentRun.Model)),

            Mode.Cancellation => throw new OperationCanceledException(cancellationToken),

            _ => throw new InvalidOperationException($"Unhandled mode '{_mode}'.")
        };
    }
}
