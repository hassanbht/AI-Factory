using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class LocalContextProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly string _projectRoot;
    private readonly Guid _projectId = Guid.NewGuid();

    public LocalContextProviderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        _projectRoot = Path.Combine(Path.GetTempPath(), "ai-factory-context-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Context Engine Test",
            Slug = "context-engine-test",
            Status = ProjectStatus.Created,
            RootPath = _projectRoot,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();

        if (Directory.Exists(_projectRoot))
        {
            Directory.Delete(_projectRoot, recursive: true);
        }
    }

    private FactoryDbContext NewDbContext() => new(_options);

    [Fact]
    public async Task QueryAsync_ReturnsRequirements_FromDatabase()
    {
        await using (var db = NewDbContext())
        {
            db.Requirements.Add(new Requirement
            {
                Id = Guid.NewGuid(),
                ProjectId = _projectId,
                Title = "Support login",
                Description = "Users must be able to log in.",
                Status = "Approved",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }

        await using var readDb = NewDbContext();
        var provider = new LocalContextProvider(readDb);

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.ProjectRequirements));

        Assert.Single(result.Documents);
        Assert.Contains("Support login", result.Documents[0].Content);
        Assert.Equal(1.0, result.Documents[0].Relevance);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmpty_WhenNoRequirementsExist()
    {
        await using var db = NewDbContext();
        var provider = new LocalContextProvider(db);

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.ProjectRequirements));

        Assert.Empty(result.Documents);
    }

    [Fact]
    public async Task QueryAsync_ReturnsArchitectureDocument_WhenFileExists()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_projectRoot, "architecture.md"), "# Architecture\n\nLayered.");

        await using var db = NewDbContext();
        var provider = new LocalContextProvider(db);

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.ArchitectureDocuments));

        Assert.Single(result.Documents);
        Assert.Contains("Layered", result.Documents[0].Content);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmpty_WhenArchitectureFileDoesNotExist()
    {
        await using var db = NewDbContext();
        var provider = new LocalContextProvider(db);

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.ArchitectureDocuments));

        Assert.Empty(result.Documents);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmpty_WhenProjectRootDoesNotExistOnDisk()
    {
        var otherProjectId = Guid.NewGuid();

        await using (var db = NewDbContext())
        {
            db.Projects.Add(new Project
            {
                Id = otherProjectId,
                Name = "No Folder",
                Slug = "no-folder",
                RootPath = Path.Combine(Path.GetTempPath(), "ai-factory-context-tests", "does-not-exist-" + Guid.NewGuid().ToString("N")),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }

        await using var readDb = NewDbContext();
        var provider = new LocalContextProvider(readDb);

        var result = await provider.QueryAsync(
            new ContextQuery(otherProjectId, ContextCategory.CodingStandards));

        Assert.Empty(result.Documents);
    }

    [Fact]
    public async Task QueryAsync_Throws_WhenProjectDoesNotExist()
    {
        await using var db = NewDbContext();
        var provider = new LocalContextProvider(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.QueryAsync(
                new ContextQuery(Guid.NewGuid(), ContextCategory.ArchitectureDocuments)));
    }

    [Fact]
    public async Task QueryAsync_ReturnsSourceFiles_OrderedByRecency()
    {
        var olderFile = Path.Combine(_projectRoot, "old.cs");
        var newerFile = Path.Combine(_projectRoot, "new.cs");

        await File.WriteAllTextAsync(olderFile, "class Old {}");
        await File.WriteAllTextAsync(newerFile, "class New {}");

        File.SetLastWriteTimeUtc(olderFile, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(newerFile, DateTime.UtcNow);

        await using var db = NewDbContext();
        var provider = new LocalContextProvider(db);

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.RelevantSourceFiles));

        Assert.Equal(2, result.Documents.Count);
        Assert.Equal("new.cs", result.Documents[0].Source);
        Assert.True(result.Documents[0].Relevance >= result.Documents[1].Relevance);
    }

    [Fact]
    public async Task QueryAsync_SourceFiles_ExcludesBuildDirectories()
    {
        var binDir = Path.Combine(_projectRoot, "bin");
        Directory.CreateDirectory(binDir);
        await File.WriteAllTextAsync(Path.Combine(binDir, "generated.cs"), "class Generated {}");

        await using var db = NewDbContext();
        var provider = new LocalContextProvider(db);

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.RelevantSourceFiles));

        Assert.Empty(result.Documents);
    }

    [Fact]
    public async Task QueryAsync_ReturnsPreviousTaskOutput_FromLatestAgentRun()
    {
        var workItemId = Guid.NewGuid();

        await using (var db = NewDbContext())
        {
            var workItem = new WorkItem
            {
                Id = workItemId,
                ProjectId = _projectId,
                Title = "Implement feature",
                Status = WorkItemStatus.Completed.ToString(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            db.WorkItems.Add(workItem);

            db.AgentRuns.Add(new AgentRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItemId,
                Agent = "coder",
                Prompt = "do it",
                Response = "Implemented successfully.",
                Success = true,
                AttemptNumber = 1,
                StartedAtUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }

        await using var readDb = NewDbContext();
        var provider = new LocalContextProvider(readDb);

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.PreviousTaskOutputs));

        Assert.Single(result.Documents);
        Assert.Contains("Implemented successfully.", result.Documents[0].Content);
    }

    [Fact]
    public async Task QueryAsync_ReturnsPreviousAgentResults_WithMetadata()
    {
        var workItemId = Guid.NewGuid();

        await using (var db = NewDbContext())
        {
            db.WorkItems.Add(new WorkItem
            {
                Id = workItemId,
                ProjectId = _projectId,
                Title = "Fix bug",
                Status = WorkItemStatus.Failed.ToString(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            db.AgentRuns.Add(new AgentRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItemId,
                Agent = "coder",
                Prompt = "fix it",
                Error = "build failed",
                Success = false,
                AttemptNumber = 1,
                StartedAtUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }

        await using var readDb = NewDbContext();
        var provider = new LocalContextProvider(readDb);

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.PreviousAgentResults));

        Assert.Single(result.Documents);
        Assert.Equal("False", result.Documents[0].Metadata["success"]);
        Assert.Contains("build failed", result.Documents[0].Content);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmpty_ForTestFailures_NoDataSourceYet()
    {
        await using var db = NewDbContext();
        var provider = new LocalContextProvider(db);

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.TestFailures));

        Assert.Empty(result.Documents);
    }
}
