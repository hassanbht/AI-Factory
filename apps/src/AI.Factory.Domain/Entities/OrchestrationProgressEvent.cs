using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

public sealed class OrchestrationProgressEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrchestrationRunId { get; set; }

    public OrchestrationRun? OrchestrationRun { get; set; }

    public OrchestrationProgressStage Stage { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public string Message { get; set; } = string.Empty;
}
