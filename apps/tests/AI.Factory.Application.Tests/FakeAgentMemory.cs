using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Tests;

/// <summary>Deterministic IAgentMemory test double — fixed search results, records what it was asked and stored.</summary>
public sealed class FakeAgentMemory : IAgentMemory
{
    private readonly IReadOnlyList<AgentMemoryEntry> _searchResults;

    public FakeAgentMemory(IReadOnlyList<AgentMemoryEntry>? searchResults = null)
    {
        _searchResults = searchResults ?? Array.Empty<AgentMemoryEntry>();
    }

    public List<AgentMemoryEntry> Stored { get; } = new();

    public AgentMemoryQuery? LastQuery { get; private set; }

    public Task StoreAsync(AgentMemoryEntry entry, CancellationToken cancellationToken = default)
    {
        Stored.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentMemoryEntry>> SearchAsync(
        AgentMemoryQuery query, CancellationToken cancellationToken = default)
    {
        LastQuery = query;
        return Task.FromResult(_searchResults);
    }
}
