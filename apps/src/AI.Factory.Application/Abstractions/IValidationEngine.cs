namespace AI.Factory.Application.Abstractions;

public interface IValidationEngine
{
    /// <summary>
    /// Builds the project; runs tests only if the build succeeded. Never
    /// requires network/interaction — a missing/invalid project path just
    /// surfaces as a normal build failure.
    /// </summary>
    Task<ValidationResult> ValidateAsync(
        ValidationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ValidationRequest(
    Guid WorkItemId,
    string ProjectPath);

/// <summary>One normalized diagnostic line. Category is "Build" or "Test".</summary>
public sealed record ValidationFailure(
    string Category,
    string Message);

public sealed record ValidationResult(
    bool Success,
    bool BuildSucceeded,
    string BuildOutput,
    bool? TestsSucceeded,
    string? TestOutput,
    IReadOnlyList<ValidationFailure> Failures);
