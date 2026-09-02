namespace AI.Factory.Domain.Entities;

public sealed class Artifact
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Guid? WorkItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string ArtifactType { get; set; } = string.Empty;

    public string? ContentHash { get; set; }

    public long? SizeBytes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
