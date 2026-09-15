using AI.Factory.Application.Abstractions;
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

    /// <summary>
    /// Phase 18.17 — helper that constructs the shared resolver with unset
    /// base directory, which exactly reproduces the prior CWD-relative
    /// Path.GetFullPath behavior for every test that seeds an absolute
    /// RootPath (Path.GetFullPath(absolutePath) == absolutePath unchanged).
    /// </summary>
    private static IWorkspaceResolver DefaultResolver() =>
        new ProjectWorkspaceResolver(new AgentWorkspaceOptions());

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
        var provider = new LocalContextProvider(readDb, DefaultResolver());

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
        var provider = new LocalContextProvider(db, DefaultResolver());

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
        var provider = new LocalContextProvider(db, DefaultResolver());

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.ArchitectureDocuments));

        Assert.Single(result.Documents);
        Assert.Contains("Layered", result.Documents[0].Content);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmpty_WhenArchitectureFileDoesNotExist()
    {
        await using var db = NewDbContext();
        var provider = new LocalContextProvider(db, DefaultResolver());

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
        var provider = new LocalContextProvider(readDb, DefaultResolver());

        var result = await provider.QueryAsync(
            new ContextQuery(otherProjectId, ContextCategory.CodingStandards));

        Assert.Empty(result.Documents);
    }

    [Fact]
    public async Task QueryAsync_Throws_WhenProjectDoesNotExist()
    {
        await using var db = NewDbContext();
        var provider = new LocalContextProvider(db, DefaultResolver());

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
        var provider = new LocalContextProvider(db, DefaultResolver());

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
        var provider = new LocalContextProvider(db, DefaultResolver());

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

            db.HermesExecutions.Add(new HermesExecution
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
        var provider = new LocalContextProvider(readDb, DefaultResolver());

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

            db.HermesExecutions.Add(new HermesExecution
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
        var provider = new LocalContextProvider(readDb, DefaultResolver());

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
        var provider = new LocalContextProvider(db, DefaultResolver());

        var result = await provider.QueryAsync(
            new ContextQuery(_projectId, ContextCategory.TestFailures));

        Assert.Empty(result.Documents);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Phase 18.17 — workspace resolver integration tests
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 18.17 §5 — proves ProjectsBaseDirectory is honored by
    /// LocalContextProvider, not ignored in favor of the process CWD.
    /// Creates a Project with a relative RootPath, configures a base
    /// directory, and confirms the provider reads the file correctly from
    /// the base-resolved absolute path.
    /// </summary>
    [Fact]
    public async Task QueryAsync_UsesWorkspaceResolver_ProjectsBaseDirectory_IsHonored()
    {
        // Arrange: base directory + relative RootPath that only resolves correctly
        // when combined with the base (not the process CWD).
        var baseDir = Path.Combine(Path.GetTempPath(), "aifcp17", Guid.NewGuid().ToString("N")[..8]);
        var relativeSlug = "proj-" + Guid.NewGuid().ToString("N")[..6];
        var absoluteProjectDir = Path.Combine(baseDir, relativeSlug);
        Directory.CreateDirectory(absoluteProjectDir);

        try
        {
            // Write a known file to the absolute path.
            await File.WriteAllTextAsync(
                Path.Combine(absoluteProjectDir, "architecture.md"), "# Base Dir Architecture");

            var relativeProjectId = Guid.NewGuid();

            await using (var db = NewDbContext())
            {
                db.Projects.Add(new Project
                {
                    Id = relativeProjectId,
                    Name = "Relative Root Project",
                    Slug = relativeSlug,
                    // Relative path — only resolves correctly against baseDir, not CWD.
                    RootPath = relativeSlug,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var options = new AgentWorkspaceOptions { ProjectsBaseDirectory = baseDir };
            var resolver = new ProjectWorkspaceResolver(options);

            await using var readDb = NewDbContext();
            var provider = new LocalContextProvider(readDb, resolver);

            // Act
            var result = await provider.QueryAsync(
                new ContextQuery(relativeProjectId, ContextCategory.ArchitectureDocuments));

            // Assert: file found via base-directory resolution.
            Assert.Single(result.Documents);
            Assert.Contains("Base Dir Architecture", result.Documents[0].Content);
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                try { Directory.Delete(baseDir, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Phase 18.17 §5 — proves that when workspace resolution fails (e.g. the
    /// workspace directory does not exist), LocalContextProvider returns empty
    /// rather than throwing — preserving the prior soft-failure behavior for
    /// filesystem-backed queries.
    /// </summary>
    [Fact]
    public async Task QueryAsync_ReturnsEmpty_WhenWorkspaceResolutionFails()
    {
        var nonExistentId = Guid.NewGuid();

        await using (var db = NewDbContext())
        {
            db.Projects.Add(new Project
            {
                Id = nonExistentId,
                Name = "Ghost Project",
                Slug = "ghost",
                // Absolute path that does not exist.
                RootPath = Path.Combine(Path.GetTempPath(), "aifcp17-ghost-" + Guid.NewGuid().ToString("N")),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var readDb = NewDbContext();
        // Default resolver: existence check will fail → IsValid = false → empty.
        var provider = new LocalContextProvider(readDb, DefaultResolver());

        var archResult = await provider.QueryAsync(
            new ContextQuery(nonExistentId, ContextCategory.ArchitectureDocuments));
        var srcResult = await provider.QueryAsync(
            new ContextQuery(nonExistentId, ContextCategory.RelevantSourceFiles));

        Assert.Empty(archResult.Documents);
        Assert.Empty(srcResult.Documents);
    }

    /// <summary>
    /// Phase 18.17 §5 — proves that when ProjectsBaseDirectory is configured,
    /// changing the process working directory does NOT alter the resolved path,
    /// because the base-directory anchor is fixed at configuration time rather
    /// than determined from the ambient CWD at resolution time.
    /// </summary>
    [Fact]
    public async Task QueryAsync_ReturnsEmpty_WhenCwdChanges_ButBaseDirectoryConfigured()
    {
        // The project's relative RootPath will only be correct under the configured base.
        var baseDir = Path.Combine(Path.GetTempPath(), "aifcp17cwd", Guid.NewGuid().ToString("N")[..8]);
        var relativeSlug = "wksp-" + Guid.NewGuid().ToString("N")[..6];
        var absoluteProjectDir = Path.Combine(baseDir, relativeSlug);
        Directory.CreateDirectory(absoluteProjectDir);

        var originalCwd = Directory.GetCurrentDirectory();

        try
        {
            var relProjectId = Guid.NewGuid();

            await using (var db = NewDbContext())
            {
                db.Projects.Add(new Project
                {
                    Id = relProjectId,
                    Name = "CWD Test Project",
                    Slug = relativeSlug,
                    RootPath = relativeSlug,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var options = new AgentWorkspaceOptions { ProjectsBaseDirectory = baseDir };
            var resolver = new ProjectWorkspaceResolver(options);

            // Resolve BEFORE changing CWD.
            await using var db1 = NewDbContext();
            var project = (await db1.Projects.FindAsync(relProjectId))!;
            var resolutionBefore = resolver.Resolve(project);

            // Change CWD to something completely different — must not affect resolution.
            Directory.SetCurrentDirectory(Path.GetTempPath());

            var resolutionAfter = resolver.Resolve(project);

            // Both resolutions must agree on the same absolute path.
            Assert.True(resolutionBefore.IsValid, resolutionBefore.FailureReason);
            Assert.True(resolutionAfter.IsValid, resolutionAfter.FailureReason);
            Assert.Equal(resolutionBefore.Path, resolutionAfter.Path);
            Assert.Equal(absoluteProjectDir, resolutionBefore.Path, ignoreCase: true);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);

            if (Directory.Exists(baseDir))
            {
                try { Directory.Delete(baseDir, recursive: true); } catch { /* best-effort */ }
            }
        }
    }
}
