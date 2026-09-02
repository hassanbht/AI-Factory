namespace AI.Factory.Domain.Entities;

public sealed class ValidationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkItemId { get; set; }

    public WorkItem? WorkItem { get; set; }

    public bool Success { get; set; }

    public bool BuildSucceeded { get; set; }

    public string BuildOutput { get; set; } = string.Empty;

    public bool? TestsSucceeded { get; set; }

    public string? TestOutput { get; set; }

    /// <summary>JSON-serialized list of normalized failures (see ValidationFailure in Application.Abstractions).</summary>
    public string? FailuresJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
