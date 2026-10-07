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

public enum ReviewChangeKind
{
    Added,
    Modified,
    Deleted
}

public sealed record ReviewFileChange(
    string Path,
    ReviewChangeKind Kind,
    string? Content = null,
    string? Diff = null);

public sealed record ReviewBundle(
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<ReviewFileChange> Changes,
    string? UnifiedDiff = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public interface IReviewBundleBuilder
{
    Task<ReviewBundle?> BuildBundleAsync(
        string workspacePath,
        IReadOnlyList<string>? changedFiles = null,
        CancellationToken cancellationToken = default);
}

public enum ReviewExecutionStatus
{
    SucceededWithFindings,
    SucceededWithNoFindings,
    ReviewerFailure,
    MalformedPayload,
    MissingResult,
    TimedOut,
    Cancelled
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
    string TaskHistoryText,
    ReviewBundle? Bundle = null);

public sealed record ReviewResult(
    Guid WorkItemId,
    bool BlocksCompletion,
    IReadOnlyList<ReviewFinding> Findings,
    ReviewExecutionStatus Status = ReviewExecutionStatus.SucceededWithNoFindings,
    string? ErrorMessage = null);
