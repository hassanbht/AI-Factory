using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Deterministic IAgentTool test double — no filesystem, no network. Mirrors
/// FakeAgentRuntime's Mode pattern for registry/execution tests.
/// </summary>
public sealed class FakeAgentTool : IAgentTool
{
    public enum Mode
    {
        Success,
        Failure,
        Throw,
        Cancel
    }

    private readonly Mode _mode;

    public FakeAgentTool(string name, Mode mode, bool enabled = true)
    {
        Name = name;
        _mode = mode;
        Enabled = enabled;
    }

    public string Name { get; }

    public string Description => "Fake tool for tests.";

    public string InputSchema => """{"type":"object"}""";

    public bool Enabled { get; }

    public int CallCount { get; private set; }

    public Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context,
        CancellationToken cancellationToken = default)
    {
        CallCount++;

        return _mode switch
        {
            Mode.Success => Task.FromResult(new AgentToolResult(true, """{"ok":true}""", null, 1)),
            Mode.Failure => Task.FromResult(new AgentToolResult(false, null, "Simulated tool failure.", 1)),
            Mode.Throw => throw new InvalidOperationException("Simulated tool exception."),
            Mode.Cancel => throw new OperationCanceledException(cancellationToken),
            _ => throw new InvalidOperationException($"Unhandled mode '{_mode}'.")
        };
    }
}
