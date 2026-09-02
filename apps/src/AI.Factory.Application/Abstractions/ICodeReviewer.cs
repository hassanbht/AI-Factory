namespace AI.Factory.Application.Abstractions;

public interface ICodeReviewer
{
    /// <summary>
    /// Runs an independent review over the assembled context. The
    /// implementation must use a model/agent independent from the one that
    /// did the implementation work, whenever possible.
    /// </summary>
    Task<ReviewResult> ReviewAsync(
        ReviewContext context,
        CancellationToken cancellationToken = default);
}

public enum ReviewFindingSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info
}

public enum ReviewCategory
{
    Correctness,
    RequirementsCompliance,
    Architecture,
    Security,
    ErrorHandling,
    TestCoverage,
    Maintainability,
    UnnecessaryComplexity
}

public sealed record ReviewFinding(
    ReviewFindingSeverity Severity,
    ReviewCategory Category,
    string? File,
    int? Line,
    string Description,
    string Recommendation);

public sealed record ReviewContext(
    Guid WorkItemId,
    Guid ProjectId,
    string RequirementsText,
    string? AcceptanceCriteria,
    string ArchitectureText,
    IReadOnlyList<string> ChangedFiles,
    string? TestOutput,
    bool? TestsSucceeded,
    string BuildOutput,
    bool BuildSucceeded,
    string TaskHistoryText);

public sealed record ReviewResult(
    Guid WorkItemId,
    bool BlocksCompletion,
    IReadOnlyList<ReviewFinding> Findings);
