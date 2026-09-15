using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ReviewContextBuilderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public ReviewContextBuilderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Review Context Test",
            Slug = "review-context-test",
            RootPath = "unused",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task BuildAsync_AssemblesAllSevenInputs()
    {
        var workItemId = Guid.NewGuid();

        await using (var db = new FactoryDbContext(_options))
        {
            db.Requirements.Add(new Requirement
            {
                Id = Guid.NewGuid(),
                ProjectId = _projectId,
                Title = "Login",
                Description = "Users must log in.",
                Status = "Approved",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            db.WorkItems.Add(new WorkItem
            {
                Id = workItemId,
                ProjectId = _projectId,
                Title = "Implement login",
                Description = "Do it.",
                AcceptanceCriteria = "Given valid credentials, user is logged in.",
                Status = WorkItemStatus.Running.ToString(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            db.Artifacts.Add(new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = _projectId,
                WorkItemId = workItemId,
                Name = "Login.cs",
                Path = "/workspace/Login.cs",
                ArtifactType = "cs",
                CreatedAtUtc = DateTime.UtcNow
            });

            db.HermesExecutions.Add(new HermesExecution
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItemId,
                Agent = "coder",
                Prompt = "implement login",
                Response = "done",
                Success = true,
                AttemptNumber = 1,
                StartedAtUtc = DateTime.UtcNow
            });

            db.ValidationRuns.Add(new ValidationRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItemId,
                Success = true,
                BuildSucceeded = true,
                BuildOutput = "Build succeeded.",
                TestsSucceeded = true,
                TestOutput = "All tests passed.",
                CreatedAtUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }

        await using var readDb = new FactoryDbContext(_options);
        var workItem = await readDb.WorkItems.SingleAsync(x => x.Id == workItemId);

        var builder = new ReviewContextBuilder(
            new LocalContextProvider(readDb, new ProjectWorkspaceResolver(new AgentWorkspaceOptions())),
            new ArtifactRepository(readDb),
            new ValidationRunRepository(readDb));

        var context = await builder.BuildAsync(workItem);

        Assert.Contains("Login", context.RequirementsText);
        Assert.Equal("Given valid credentials, user is logged in.", context.AcceptanceCriteria);
        Assert.Contains("/workspace/Login.cs", context.ChangedFiles);
        Assert.True(context.BuildSucceeded);
        Assert.Contains("Build succeeded.", context.BuildOutput);
        Assert.True(context.TestsSucceeded);
        Assert.Contains("All tests passed.", context.TestOutput);
        Assert.Contains("coder", context.TaskHistoryText);
    }

    [Fact]
    public async Task BuildAsync_DegradesGracefully_WhenNoValidationRunExistsYet()
    {
        var workItemId = Guid.NewGuid();

        await using (var db = new FactoryDbContext(_options))
        {
            db.WorkItems.Add(new WorkItem
            {
                Id = workItemId,
                ProjectId = _projectId,
                Title = "Task with no validation yet",
                Status = WorkItemStatus.Running.ToString(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }

        await using var readDb = new FactoryDbContext(_options);
        var workItem = await readDb.WorkItems.SingleAsync(x => x.Id == workItemId);

        var builder = new ReviewContextBuilder(
            new LocalContextProvider(readDb, new ProjectWorkspaceResolver(new AgentWorkspaceOptions())),
            new ArtifactRepository(readDb),
            new ValidationRunRepository(readDb));

        var context = await builder.BuildAsync(workItem);

        Assert.False(context.BuildSucceeded);
        Assert.Null(context.TestsSucceeded);
        Assert.Empty(context.ChangedFiles);
    }
}
