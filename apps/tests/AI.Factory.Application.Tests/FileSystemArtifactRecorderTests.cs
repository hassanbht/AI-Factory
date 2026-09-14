using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.14 — with workspacePath now able to be an entire live Project
/// directory (not just a small scratch folder), build output / VCS metadata
/// must never be indexed as Artifact rows, or every execution would insert
/// thousands of them. Proves the exclusion list added to
/// RecordWorkspaceArtifactsAsync.
/// </summary>
public sealed class FileSystemArtifactRecorderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly string _workspaceDir;

    public FileSystemArtifactRecorderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        _workspaceDir = Path.Combine(Path.GetTempPath(), "ai-factory-artifact-recorder-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceDir);
    }

    public void Dispose()
    {
        _connection.Dispose();

        if (Directory.Exists(_workspaceDir))
        {
            Directory.Delete(_workspaceDir, recursive: true);
        }
    }

    [Fact]
    public async Task RecordWorkspaceArtifactsAsync_SkipsFilesUnderExcludedDirectories_ButRecordsOrdinarySourceFiles()
    {
        WriteFile("src/Program.cs", "// source");
        WriteFile("bin/Debug/app.dll", "binary");
        WriteFile("obj/Debug/app.cache", "cache");
        WriteFile("node_modules/pkg/index.js", "js");
        WriteFile(".git/HEAD", "ref: refs/heads/main");
        WriteFile(".vs/state.json", "{}");

        var projectId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();

        await using (var db = new FactoryDbContext(_options))
        {
            var recorder = new FileSystemArtifactRecorder(db);
            await recorder.RecordWorkspaceArtifactsAsync(projectId, workItemId, _workspaceDir);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var artifacts = await new ArtifactRepository(verifyDb).GetByWorkItemIdAsync(workItemId);

        Assert.Single(artifacts);
        Assert.Equal("Program.cs", artifacts[0].Name);
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_workspaceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
