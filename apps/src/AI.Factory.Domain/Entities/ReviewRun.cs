namespace AI.Factory.Domain.Entities;

public sealed class ReviewRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkItemId { get; set; }

    public WorkItem? WorkItem { get; set; }

    public bool BlocksCompletion { get; set; }

    /// <summary>JSON-serialized list of findings (see ReviewFinding in Application.Abstractions).</summary>
    public string FindingsJson { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
