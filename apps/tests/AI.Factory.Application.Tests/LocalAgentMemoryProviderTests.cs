using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class LocalAgentMemoryProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public LocalAgentMemoryProviderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static AgentMemoryEntry Entry(
        Guid? projectId, Guid? agentDefinitionId = null, Guid? agentRunId = null,
        string type = "note", string content = "content",
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(Guid.NewGuid(), projectId, agentDefinitionId, agentRunId, type, content, metadata, DateTime.UtcNow);

    [Fact]
    public async Task StoreAsync_ThenSearchAsync_RetrievesTheStoredEntry()
    {
        var projectId = Guid.NewGuid();
        var metadata = new Dictionary<string, string> { ["source"] = "unit-test" };

        await using var db = new FactoryDbContext(_options);
        var memory = new LocalAgentMemoryProvider(db);

        await memory.StoreAsync(Entry(projectId, content: "the build failed on step 3", metadata: metadata));

        var results = await memory.SearchAsync(new AgentMemoryQuery(ProjectId: projectId));

        Assert.Single(results);
        Assert.Equal("the build failed on step 3", results[0].Content);
        Assert.Equal("unit-test", results[0].Metadata?["source"]);
        Assert.NotEqual(default, results[0].CreatedAtUtc);
    }

    [Fact]
    public async Task SearchAsync_ReturnsMissingScope_AsEmptyResult()
    {
        await using var db = new FactoryDbContext(_options);
        var memory = new LocalAgentMemoryProvider(db);

        var results = await memory.SearchAsync(new AgentMemoryQuery(ProjectId: Guid.NewGuid()));

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_RespectsMaxResults()
    {
        var projectId = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);
        var memory = new LocalAgentMemoryProvider(db);

        for (var i = 0; i < 5; i++)
        {
            await memory.StoreAsync(Entry(projectId, content: $"entry-{i}"));
        }

        var results = await memory.SearchAsync(new AgentMemoryQuery(ProjectId: projectId, MaxResults: 2));

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_OrdersDeterministically_NewestFirst()
    {
        var projectId = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);
        var memory = new LocalAgentMemoryProvider(db);

        var older = Entry(projectId, content: "older") with { CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10) };
        var newer = Entry(projectId, content: "newer") with { CreatedAtUtc = DateTime.UtcNow };

        await memory.StoreAsync(older);
        await memory.StoreAsync(newer);

        var results = await memory.SearchAsync(new AgentMemoryQuery(ProjectId: projectId));

        Assert.Equal("newer", results[0].Content);
        Assert.Equal("older", results[1].Content);
    }

    [Fact]
    public async Task SearchAsync_FiltersBySearchText()
    {
        var projectId = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);
        var memory = new LocalAgentMemoryProvider(db);

        await memory.StoreAsync(Entry(projectId, content: "the deploy succeeded"));
        await memory.StoreAsync(Entry(projectId, content: "the test suite is flaky"));

        var results = await memory.SearchAsync(new AgentMemoryQuery(ProjectId: projectId, SearchText: "flaky"));

        Assert.Single(results);
        Assert.Equal("the test suite is flaky", results[0].Content);
    }

    [Fact]
    public async Task SearchAsync_IsolatesMemoryBetweenProjects()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);
        var memory = new LocalAgentMemoryProvider(db);

        await memory.StoreAsync(Entry(projectA, content: "project A secret plan"));
        await memory.StoreAsync(Entry(projectB, content: "project B secret plan"));

        var resultsForA = await memory.SearchAsync(new AgentMemoryQuery(ProjectId: projectA));

        Assert.Single(resultsForA);
        Assert.Equal("project A secret plan", resultsForA[0].Content);
    }

    [Fact]
    public async Task SearchAsync_IsolatesMemoryByAgentRun_WithinTheSameProject()
    {
        var projectId = Guid.NewGuid();
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);
        var memory = new LocalAgentMemoryProvider(db);

        await memory.StoreAsync(Entry(projectId, agentRunId: runA, content: "run A note"));
        await memory.StoreAsync(Entry(projectId, agentRunId: runB, content: "run B note"));

        var results = await memory.SearchAsync(new AgentMemoryQuery(ProjectId: projectId, AgentRunId: runA));

        Assert.Single(results);
        Assert.Equal("run A note", results[0].Content);
    }

    [Fact]
    public async Task SearchAsync_GlobalMemory_OnlyReturnedWhenExplicitlyRequested()
    {
        var projectId = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);
        var memory = new LocalAgentMemoryProvider(db);

        await memory.StoreAsync(Entry(projectId: null, content: "global lesson"));
        await memory.StoreAsync(Entry(projectId, content: "project-scoped note"));

        var globalResults = await memory.SearchAsync(new AgentMemoryQuery(ProjectId: null));
        var projectResults = await memory.SearchAsync(new AgentMemoryQuery(ProjectId: projectId));

        Assert.Single(globalResults);
        Assert.Equal("global lesson", globalResults[0].Content);
        Assert.Single(projectResults);
        Assert.Equal("project-scoped note", projectResults[0].Content);
    }

    [Fact]
    public async Task SearchAsync_RespectsCancellation()
    {
        await using var db = new FactoryDbContext(_options);
        var memory = new LocalAgentMemoryProvider(db);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => memory.SearchAsync(new AgentMemoryQuery(ProjectId: Guid.NewGuid()), cts.Token));
    }
}
