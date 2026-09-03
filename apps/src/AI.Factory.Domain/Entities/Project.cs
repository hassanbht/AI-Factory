using AI.Factory.Domain.Common;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

public sealed class Project : IVersionedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Created;

    public string RootPath { get; set; } = string.Empty;

    public string? GitRepository { get; set; }

    public string? GitBranch { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public long Version { get; set; } = 1;
}
