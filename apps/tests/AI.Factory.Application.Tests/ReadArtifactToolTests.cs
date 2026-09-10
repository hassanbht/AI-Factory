using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ReadArtifactToolTests : IDisposable
{
    private sealed class FakeArtifactRepository : IArtifactRepository
    {
        private readonly Dictionary<Guid, Artifact> _artifacts = new();

        public void Add(Artifact artifact) => _artifacts[artifact.Id] = artifact;

        public Task<IReadOnlyList<Artifact>> GetByWorkItemIdAsync(
            Guid workItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Artifact>>(_artifacts.Values.Where(x => x.WorkItemId == workItemId).ToList());

        public Task<Artifact?> GetByIdAsync(Guid artifactId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_artifacts.GetValueOrDefault(artifactId));
    }

    private readonly string _workspaceRoot;
    private readonly FakeArtifactRepository _repository = new();

    public ReadArtifactToolTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    private ReadArtifactTool CreateTool() =>
        new(_repository, Options.Create(new HermesOptions { WorkspaceRoot = _workspaceRoot }),
            NullLogger<ReadArtifactTool>.Instance);

    private static AgentToolContext ContextFor(string inputJson) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), inputJson);

    [Fact]
    public async Task ExecuteAsync_ReturnsContent_ForArtifactInsideWorkspace()
    {
        var filePath = Path.Combine(_workspaceRoot, "notes.txt");
        await File.WriteAllTextAsync(filePath, "hello world");

        var artifactId = Guid.NewGuid();
        _repository.Add(new Artifact { Id = artifactId, Name = "notes.txt", Path = filePath, ArtifactType = "txt" });

        var result = await CreateTool().ExecuteAsync(ContextFor($$"""{"artifactId":"{{artifactId}}"}"""));

        Assert.True(result.Success);
        Assert.Contains("hello world", result.OutputJson);
    }

    [Fact]
    public async Task ExecuteAsync_Fails_WhenArtifactNotFound()
    {
        var result = await CreateTool().ExecuteAsync(ContextFor($$"""{"artifactId":"{{Guid.NewGuid()}}"}"""));

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_Fails_ForMissingArtifactId()
    {
        var result = await CreateTool().ExecuteAsync(ContextFor("{}"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_Fails_ForInvalidJson()
    {
        var result = await CreateTool().ExecuteAsync(ContextFor("not json"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsPath_OutsideWorkspaceRoot()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), "ai-factory-tests-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outsidePath, "should not be readable");

        try
        {
            var artifactId = Guid.NewGuid();
            _repository.Add(new Artifact { Id = artifactId, Name = "outside.txt", Path = outsidePath, ArtifactType = "txt" });

            var result = await CreateTool().ExecuteAsync(ContextFor($$"""{"artifactId":"{{artifactId}}"}"""));

            Assert.False(result.Success);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsSecretShapedFilename()
    {
        var filePath = Path.Combine(_workspaceRoot, ".env");
        await File.WriteAllTextAsync(filePath, "API_KEY=super-secret");

        var artifactId = Guid.NewGuid();
        _repository.Add(new Artifact { Id = artifactId, Name = ".env", Path = filePath, ArtifactType = "" });

        var result = await CreateTool().ExecuteAsync(ContextFor($$"""{"artifactId":"{{artifactId}}"}"""));

        Assert.False(result.Success);
        Assert.DoesNotContain("super-secret", result.ErrorMessage ?? string.Empty);
    }
}
