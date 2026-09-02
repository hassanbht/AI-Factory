using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Persistence;

namespace AI.Factory.Infrastructure.Agents;

public sealed class FileSystemArtifactRecorder : IArtifactRecorder
{
    private readonly FactoryDbContext _db;

    public FileSystemArtifactRecorder(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task RecordWorkspaceArtifactsAsync(
        Guid projectId,
        Guid workItemId,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return;
        }

        var files = Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories);

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(filePath);

            await _db.Artifacts.AddAsync(new Artifact
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                WorkItemId = workItemId,
                Name = fileInfo.Name,
                Path = filePath,
                ArtifactType = fileInfo.Extension.TrimStart('.'),
                SizeBytes = fileInfo.Length,
                CreatedAtUtc = DateTime.UtcNow
            }, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
