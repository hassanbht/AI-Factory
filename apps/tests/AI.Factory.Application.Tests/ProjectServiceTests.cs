using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Projects;
using AI.Factory.Domain.Enums;
using AI.Factory.Domain.Exceptions;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.2 §15 — Project creation/retrieval/update/lifecycle.</summary>
public sealed class ProjectServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public ProjectServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private ProjectService CreateService(FactoryDbContext db) => new(new ProjectRepository(db));

    [Fact]
    public async Task CreateAsync_ValidName_PersistsAsCreated()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await CreateService(db).CreateAsync("My Project", "desc");

        Assert.Equal(ProjectStatus.Created, project.Status);
        Assert.Equal("my-project", project.Slug);
    }

    [Fact]
    public async Task GetAsync_ExistingProject_ReturnsIt()
    {
        Guid id;
        await using (var db = new FactoryDbContext(_options))
        {
            id = (await CreateService(db).CreateAsync("Findable", "desc")).Id;
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var found = await CreateService(verifyDb).GetAsync(id);
        Assert.NotNull(found);
        Assert.Equal("Findable", found!.Name);
    }

    [Fact]
    public async Task GetAsync_UnknownProject_ReturnsNull()
    {
        await using var db = new FactoryDbContext(_options);
        Assert.Null(await CreateService(db).GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ListAsync_ReturnsAllCreatedProjects()
    {
        await using (var db = new FactoryDbContext(_options))
        {
            var service = CreateService(db);
            await service.CreateAsync("P1", "d");
            await service.CreateAsync("P2", "d");
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var all = await CreateService(verifyDb).ListAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task TransitionStatusAsync_ValidTransition_Persists()
    {
        Guid id;
        await using (var db = new FactoryDbContext(_options))
        {
            id = (await CreateService(db).CreateAsync("Transitionable", "d")).Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateService(db).TransitionStatusAsync(id, ProjectStatus.Analyzing);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await CreateService(verifyDb).GetAsync(id);
        Assert.Equal(ProjectStatus.Analyzing, reloaded!.Status);
    }

    [Fact]
    public async Task TransitionStatusAsync_InvalidTransition_Throws()
    {
        Guid id;
        await using (var db = new FactoryDbContext(_options))
        {
            id = (await CreateService(db).CreateAsync("Blocked", "d")).Id;
        }

        await using var db2 = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<InvalidProjectTransitionException>(
            () => CreateService(db2).TransitionStatusAsync(id, ProjectStatus.Completed));
    }

    [Fact]
    public async Task TransitionStatusAsync_UnknownProject_ThrowsNotFound()
    {
        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService(db).TransitionStatusAsync(Guid.NewGuid(), ProjectStatus.Analyzing));
    }
}
