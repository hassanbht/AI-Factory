using AI.Factory.Application.Agents;
using AI.Factory.Application.Exceptions;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.1.6 — IAgentDefinitionService coverage.</summary>
public sealed class AgentDefinitionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public AgentDefinitionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AgentDefinitionService CreateService(FactoryDbContext db) =>
        new(new AgentDefinitionRepository(db), NullLogger<AgentDefinitionService>.Instance);

    [Fact]
    public async Task CreateAsync_ValidCommand_PersistsEnabledDefinitionByDefault()
    {
        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);

        var definition = await service.CreateAsync(new CreateAgentDefinitionCommand("Coder", "desc", "Coding", "prompt"));

        Assert.True(definition.Enabled);
        Assert.Equal("Coder", definition.Name);
    }

    [Fact]
    public async Task CreateAsync_EmptyName_ThrowsValidation()
    {
        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);

        await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateAsync(new CreateAgentDefinitionCommand("", null, "Coding", null)));
    }

    [Fact]
    public async Task CreateAsync_EmptyAgentType_ThrowsValidation()
    {
        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);

        await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateAsync(new CreateAgentDefinitionCommand("Coder", null, "", null)));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_ForUnknownId()
    {
        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);

        Assert.Null(await service.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ListAsync_IncludesCreatedDefinitions_OrderedByName()
    {
        await using (var db = new FactoryDbContext(_options))
        {
            var service = CreateService(db);
            await service.CreateAsync(new CreateAgentDefinitionCommand("Zeta", null, "Coding", null));
            await service.CreateAsync(new CreateAgentDefinitionCommand("Alpha", null, "Coding", null));
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var list = await CreateService(verifyDb).ListAsync();

        Assert.True(list.Count >= 2);
        var names = list.Select(x => x.Name).ToList();
        Assert.True(names.IndexOf("Alpha") < names.IndexOf("Zeta"));
    }
}
