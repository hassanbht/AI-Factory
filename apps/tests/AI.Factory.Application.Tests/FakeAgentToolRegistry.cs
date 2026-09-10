using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Tests;

/// <summary>Deterministic IAgentToolRegistry test double — fixed descriptor list, no real tool execution.</summary>
public sealed class FakeAgentToolRegistry : IAgentToolRegistry
{
    private readonly IReadOnlyList<AgentToolDescriptor> _descriptors;

    public FakeAgentToolRegistry(params string[] toolNames)
    {
        _descriptors = toolNames
            .Select(name => new AgentToolDescriptor(name, "fake tool", "{}", true))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<AgentToolDescriptor> GetDescriptors() => _descriptors;

    public Task<AgentToolResult> ExecuteAsync(
        string toolName, AgentToolContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentToolResult(true, null, null, 0));
}
