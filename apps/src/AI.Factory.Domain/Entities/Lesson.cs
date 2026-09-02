using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

public sealed class Lesson
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public string TaskType { get; set; } = string.Empty;

    public string Problem { get; set; } = string.Empty;

    public string Solution { get; set; } = string.Empty;

    public string Evidence { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public string Source { get; set; } = string.Empty;

    public KnowledgeLevel Level { get; set; } = KnowledgeLevel.Observed;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
