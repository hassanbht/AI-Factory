using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Classifies an already-caught exception or a known failure reason into a
/// FailureCategory. Conservative by design — anything not recognized is
/// Unknown, never guessed, per Phase 17.3's "do not falsely classify
/// unknown failures" requirement.
/// </summary>
public interface IFailureClassifier
{
    FailureClassificationResult Classify(Exception exception);

    FailureClassificationResult Classify(string reason, string? hintCategory = null);
}

public sealed record FailureClassificationResult(FailureCategory Category, bool IsTransient, string Summary);

public sealed class FailureClassifier : IFailureClassifier
{
    public FailureClassificationResult Classify(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => new FailureClassificationResult(FailureCategory.Cancelled, true, "Operation was cancelled."),
            TimeoutException => new FailureClassificationResult(FailureCategory.Timeout, true, exception.Message),
            System.IO.IOException => new FailureClassificationResult(FailureCategory.Infrastructure, true, exception.Message),
            System.Net.Http.HttpRequestException => new FailureClassificationResult(FailureCategory.Provider, true, exception.Message),
            UnauthorizedAccessException => new FailureClassificationResult(FailureCategory.Infrastructure, false, exception.Message),
            ArgumentException => new FailureClassificationResult(FailureCategory.Validation, false, exception.Message),
            _ => new FailureClassificationResult(FailureCategory.Unknown, false, exception.Message)
        };
    }

    public FailureClassificationResult Classify(string reason, string? hintCategory = null)
    {
        // hintCategory is an existing, already-known classification (e.g. a
        // ValidationFailure.Category string, or a PipelineFailureCategory
        // name) — mapped conservatively, never inferred from the free-text
        // reason itself.
        return hintCategory?.Trim().ToLowerInvariant() switch
        {
            "build" => new FailureClassificationResult(FailureCategory.Build, false, reason),
            "test" => new FailureClassificationResult(FailureCategory.Test, false, reason),
            "validation" => new FailureClassificationResult(FailureCategory.Validation, false, reason),
            "agentexecution" or "agent" => new FailureClassificationResult(FailureCategory.Agent, false, reason),
            "review" => new FailureClassificationResult(FailureCategory.Review, false, reason),
            "humanrejection" or "approval" => new FailureClassificationResult(FailureCategory.Approval, false, reason),
            "infrastructure" => new FailureClassificationResult(FailureCategory.Infrastructure, true, reason),
            "repair" => new FailureClassificationResult(FailureCategory.Infrastructure, true, reason),
            "timeout" => new FailureClassificationResult(FailureCategory.Timeout, true, reason),
            "cancelled" or "canceled" => new FailureClassificationResult(FailureCategory.Cancelled, true, reason),
            _ => new FailureClassificationResult(FailureCategory.Unknown, false, reason)
        };
    }
}
