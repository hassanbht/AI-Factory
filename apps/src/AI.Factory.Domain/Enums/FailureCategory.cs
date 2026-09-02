namespace AI.Factory.Domain.Enums;

/// <summary>
/// Cross-cutting failure classification usable on raw exceptions, exit
/// codes and validation results alike — distinct from PipelineFailureCategory/
/// OrchestrationFailureCategory, which classify already-known pipeline
/// outcomes rather than arbitrary unstructured failures (e.g. an exception
/// caught by GlobalExceptionMiddleware with no pipeline context at all).
/// </summary>
public enum FailureCategory
{
    Unknown,
    Validation,
    Build,
    Test,
    Agent,
    Model,
    Provider,
    Timeout,
    Cancelled,
    Infrastructure,
    Approval,
    Review
}
