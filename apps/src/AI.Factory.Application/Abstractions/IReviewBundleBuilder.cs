using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>Phase P.1 — provider-neutral review input contract.</summary>
public interface IReviewBundleBuilder
{
    /// <summary>
    /// Builds the immutable review payload for one WorkItem from Factory state
    /// (the resolved workspace's baseline-to-final change set plus the review
    /// context). Never returns null: when no diff can be produced (no git
    /// baseline, unresolvable workspace), the bundle still carries the full
    /// review context and states explicitly that the change diff is unavailable —
    /// a reviewer is never silently handed less than the contract promises.
    /// </summary>
    Task<ReviewBundle> BuildAsync(
        Project project,
        ReviewContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>How one file changed between the captured baseline and the reviewed workspace.</summary>
public enum ReviewChangeKind
{
    Added,
    Modified,
    Deleted
}

/// <param name="Path">Workspace-relative, '/'-separated path of the changed file.</param>
/// <param name="BaselineContent">Content at the baseline (captured HEAD) for modified/deleted files;
/// null when unavailable (e.g. the file exceeds the scan limit). Null for added files by definition.</param>
/// <param name="FinalContent">Current workspace content for added/modified files; null for deleted
/// files (the diff representation carries what was removed) or when the content could not be read.</param>
public sealed record ReviewFileChange(
    string Path,
    ReviewChangeKind Kind,
    string? BaselineContent,
    string? FinalContent);

/// <summary>
/// Phase P.1 — the complete, reproducible input for an independent review. The bundle is generated
/// from Factory state (never from the reviewer's environment), is immutable once built, and is
/// delivered to the reviewer as files in the reviewer's own isolated workspace — never on a
/// command line, never by mounting the implementation workspace.
/// </summary>
/// <param name="Source">
/// Where the change set came from: <see cref="SourceGitBaseline"/> (baseline-to-final diff from the
/// workspace's git baseline) or <see cref="SourceContextOnly"/> (no diff could be produced; stated
/// explicitly in the bundle).
/// </param>
/// <param name="Files">Every changed file with its exact baseline and final content where available.</param>
/// <param name="ContextMarkdown">Requirements, acceptance criteria, architecture, task history and
/// build/test results — everything needed to understand what was requested and verified.</param>
/// <param name="ChangesMarkdown">The exact source changes: per file, kind, unified diff (or the full
/// before/after content when a diff cannot be produced).</param>
public sealed record ReviewBundle(
    string Source,
    IReadOnlyList<ReviewFileChange> Files,
    string ContextMarkdown,
    string ChangesMarkdown)
{
    public const string SourceGitBaseline = "git-baseline";
    public const string SourceContextOnly = "context-only";

    /// <summary>Workspace-relative file name of the context document inside the reviewer's workspace.</summary>
    public const string ContextFileName = "REVIEW/context.md";

    /// <summary>Workspace-relative file name of the change document inside the reviewer's workspace.</summary>
    public const string ChangesFileName = "REVIEW/changes.md";

    /// <summary>The bundle as provider-neutral seed files written into the reviewer's workspace.</summary>
    public IReadOnlyDictionary<string, string> ToSeedFiles() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ContextFileName] = ContextMarkdown,
        [ChangesFileName] = ChangesMarkdown
    };
}
