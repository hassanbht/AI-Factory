using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.29.1 — the review prompt must stay launchable.
///
/// The prompt reaches Hermes as a `docker run ... -z &lt;prompt&gt;` argument, and its
/// changed-file list comes from FileSystemArtifactRecorder, which records EVERY file
/// in the workspace. A live run whose workspace picked up a vendored .NET SDK produced
/// thousands of artifact rows, and the resulting command line exceeded the OS limit:
/// "Failed to start process 'docker': Argument list too long". The independent review
/// stage did not degrade — it could not run at all.
/// </summary>
public sealed class ReviewContextChangedFilesBoundTests
{
    private sealed class StubContextProvider : IContextProvider
    {
        public Task<ContextResult> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContextResult(Array.Empty<ContextDocument>()));
    }

    private sealed class StubArtifactRepository : IArtifactRepository
    {
        private readonly List<Artifact> _artifacts;

        public StubArtifactRepository(int count)
        {
            _artifacts = Enumerable.Range(0, count)
                .Select(i => new Artifact { Id = Guid.NewGuid(), Path = $"vendored/file-{i}.cs" })
                .ToList();
        }

        public Task<IReadOnlyList<Artifact>> GetByWorkItemIdAsync(
            Guid workItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Artifact>>(_artifacts);

        public Task<Artifact?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class StubValidationRunRepository : IValidationRunRepository
    {
        public Task<ValidationRun?> GetLatestAsync(Guid workItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ValidationRun?>(null);

        public Task PersistAsync(ValidationRun run, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<ValidationRun>> GetHistoryAsync(
            Guid workItemId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private static async Task<ReviewContext> BuildWithArtifactCountAsync(int count)
    {
        var builder = new ReviewContextBuilder(
            new StubContextProvider(),
            new StubArtifactRepository(count),
            new StubValidationRunRepository());

        return await builder.BuildAsync(new WorkItem { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() });
    }

    [Fact]
    public async Task ASmallChangeset_IsPassedThroughWhole()
    {
        var context = await BuildWithArtifactCountAsync(12);

        Assert.Equal(12, context.ChangedFiles.Count);
        Assert.DoesNotContain(context.ChangedFiles, f => f.Contains("not listed"));
    }

    [Fact]
    public async Task AnOversizedChangeset_IsBounded()
    {
        var context = await BuildWithArtifactCountAsync(5000);

        // Bounded list plus exactly one truncation marker.
        Assert.Equal(ReviewContextBuilder.MaxChangedFilesInPrompt + 1, context.ChangedFiles.Count);
    }

    [Fact]
    public async Task Truncation_IsStatedRatherThanSilent()
    {
        var context = await BuildWithArtifactCountAsync(5000);

        // A reviewer told nothing would assume the list is the whole changeset and
        // could report "no findings" over files it never saw.
        var marker = context.ChangedFiles[^1];
        Assert.Contains("not listed", marker);
        Assert.Contains($"{5000 - ReviewContextBuilder.MaxChangedFilesInPrompt}", marker);
    }
}
