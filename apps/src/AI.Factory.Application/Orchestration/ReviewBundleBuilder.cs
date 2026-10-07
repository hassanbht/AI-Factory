using System.Text;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase P.1 — builds the <see cref="ReviewBundle"/> (the provider-neutral review input) from
/// Factory state: the resolved workspace's baseline-to-final change set, plus the review context.
/// The reviewer never sees this code — it only sees the rendered documents, delivered as files in
/// its own isolated workspace.
/// </summary>
public sealed class ReviewBundleBuilder : IReviewBundleBuilder
{
    private const int MaxContextChars = 256_000;
    private const int MaxChangesChars = 4_000_000;

    private readonly IWorkspaceResolver _workspaceResolver;
    private readonly IWorkspaceIntegrityInspector _integrityInspector;

    public ReviewBundleBuilder(
        IWorkspaceResolver workspaceResolver,
        IWorkspaceIntegrityInspector integrityInspector)
    {
        _workspaceResolver = workspaceResolver;
        _integrityInspector = integrityInspector;
    }

    public async Task<ReviewBundle> BuildAsync(
        Project project,
        ReviewContext context,
        CancellationToken cancellationToken = default)
    {
        var files = new List<ReviewFileChange>();
        var source = ReviewBundle.SourceContextOnly;
        string? diffNote = null;

        var resolution = _workspaceResolver.Resolve(project);

        if (resolution.IsValid && resolution.Path is { } workspacePath)
        {
            var changes = await _integrityInspector.GetChangesAsync(workspacePath, cancellationToken);

            if (changes is null)
            {
                diffNote = "The workspace has no git baseline, so a baseline-to-final diff could not be produced.";
            }
            else
            {
                source = ReviewBundle.SourceGitBaseline;

                foreach (var change in changes.OrderBy(c => c.Path, StringComparer.Ordinal))
                {
                    switch (change.Kind)
                    {
                        case WorkspaceChangeKind.Added:
                            files.Add(new ReviewFileChange(change.Path, ReviewChangeKind.Added, null, change.CurrentContent));
                            break;

                        case WorkspaceChangeKind.Modified:
                            files.Add(new ReviewFileChange(change.Path, ReviewChangeKind.Modified, change.BaselineContent, change.CurrentContent));
                            break;

                        case WorkspaceChangeKind.Deleted:
                            files.Add(new ReviewFileChange(change.Path, ReviewChangeKind.Deleted, change.BaselineContent, null));
                            break;

                        case WorkspaceChangeKind.Ignored:
                            // Git-ignored files are listed in the context document but their CONTENT
                            // is deliberately never copied into the bundle: an ignored file is the
                            // classic location for local credentials, and the reviewer must not
                            // receive it.
                            break;
                    }
                }

                var ignored = changes
                    .Where(c => c.Kind == WorkspaceChangeKind.Ignored)
                    .Select(c => c.Path)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();

                if (ignored.Count > 0)
                {
                    diffNote = string.Join(Environment.NewLine,
                        new[] { diffNote, "Present in the workspace but git-ignored (contents withheld from this bundle): "
                            + string.Join(", ", ignored) }
                        .Where(static line => !string.IsNullOrEmpty(line)));
                }
            }
        }
        else
        {
            diffNote = $"The project workspace could not be resolved ({resolution.FailureReason ?? "unknown reason"}), "
                + "so a baseline-to-final diff could not be produced.";
        }

        var changesMarkdown = RenderChanges(files, diffNote);
        var contextMarkdown = RenderContext(context, source, files, diffNote);

        return new ReviewBundle(source, files, contextMarkdown, changesMarkdown);
    }

    private static string RenderChanges(IReadOnlyList<ReviewFileChange> files, string? note)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# Exact source changes (baseline -> final workspace)");
        builder.AppendLine();
        builder.AppendLine("Each file below shows the exact change: a unified diff when both versions are");
        builder.AppendLine("available, otherwise the full available content. Nothing is summarised.");
        builder.AppendLine();

        if (note is not null)
        {
            builder.AppendLine($"> NOTE: {note.Trim()}");
            builder.AppendLine();
        }

        if (files.Count == 0)
        {
            builder.AppendLine("(No changed files were detected against the baseline.)");
            return Cap(builder, MaxChangesChars, "changes");
        }

        foreach (var file in files)
        {
            builder.AppendLine($"## {file.Path} [{file.Kind.ToString().ToUpperInvariant()}]");
            builder.AppendLine();

            if (file.Kind == ReviewChangeKind.Modified
                && file.BaselineContent is not null && file.FinalContent is not null)
            {
                builder.Append(UnifiedDiff.Compute(file.Path, file.BaselineContent, file.FinalContent));
            }
            else if (file.Kind == ReviewChangeKind.Added && file.FinalContent is not null)
            {
                builder.Append(UnifiedDiff.Compute(file.Path, null, file.FinalContent));
            }
            else if (file.Kind == ReviewChangeKind.Deleted && file.BaselineContent is not null)
            {
                builder.Append(UnifiedDiff.Compute(file.Path, file.BaselineContent, null));
            }
            else
            {
                // Exact content could not be read (binary, link, oversized or unreadable).
                builder.AppendLine("> Content could not be read for this file (binary, symbolic link, oversized or unreadable).");

                if (file.BaselineContent is not null)
                {
                    builder.AppendLine();
                    builder.AppendLine("Baseline content was read:");
                    builder.AppendLine();
                    builder.AppendLine("```");
                    builder.AppendLine(Truncate(file.BaselineContent, 200_000));
                    builder.AppendLine("```");
                }

                if (file.FinalContent is not null)
                {
                    builder.AppendLine();
                    builder.AppendLine("Final content was read:");
                    builder.AppendLine();
                    builder.AppendLine("```");
                    builder.AppendLine(Truncate(file.FinalContent, 200_000));
                    builder.AppendLine("```");
                }
            }

            builder.AppendLine();
        }

        return Cap(builder, MaxChangesChars, "changes");
    }

    private static string RenderContext(
        ReviewContext context,
        string source,
        IReadOnlyList<ReviewFileChange> files,
        string? note)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# Independent review context");
        builder.AppendLine();
        builder.AppendLine("This bundle is the complete input for an independent code review. It was");
        builder.AppendLine("generated by the Factory from its own state; the reviewer did not produce it.");
        builder.AppendLine();
        builder.AppendLine($"- WorkItem: {context.WorkItemId}");
        builder.AppendLine($"- Project: {context.ProjectId}");
        builder.AppendLine($"- Change source: {source}");
        builder.AppendLine($"- Changed files: {files.Count}");
        builder.AppendLine();

        if (note is not null)
        {
            builder.AppendLine($"> NOTE: {note.Trim()}");
            builder.AppendLine();
        }

        if (files.Count > 0)
        {
            builder.AppendLine("## Changed file list");
            builder.AppendLine();
            builder.AppendLine("| Path | Kind |");
            builder.AppendLine("| --- | --- |");

            foreach (var file in files)
            {
                builder.AppendLine($"| {file.Path} | {file.Kind} |");
            }

            builder.AppendLine();
        }

        builder.AppendLine("## Requirements");
        builder.AppendLine();
        builder.AppendLine(context.RequirementsText);
        builder.AppendLine();

        builder.AppendLine("## Acceptance criteria");
        builder.AppendLine();
        builder.AppendLine(context.AcceptanceCriteria ?? "(none provided)");
        builder.AppendLine();

        builder.AppendLine("## Architecture");
        builder.AppendLine();
        builder.AppendLine(context.ArchitectureText);
        builder.AppendLine();

        builder.AppendLine("## Verification results (from the Factory's verification stage)");
        builder.AppendLine();
        builder.AppendLine($"- Build succeeded: {context.BuildSucceeded}");
        builder.AppendLine($"- Tests succeeded: {context.TestsSucceeded?.ToString() ?? "(not run)"}");
        builder.AppendLine();
        builder.AppendLine("Build output:");
        builder.AppendLine();
        builder.AppendLine(Truncate(context.BuildOutput, 32_000));
        builder.AppendLine();
        builder.AppendLine("Test output:");
        builder.AppendLine();
        builder.AppendLine(Truncate(context.TestOutput, 32_000));
        builder.AppendLine();

        builder.AppendLine("## Task history");
        builder.AppendLine();
        builder.AppendLine(Truncate(context.TaskHistoryText, 32_000));

        return Cap(builder, MaxContextChars, "context");
    }

    private static string Cap(StringBuilder builder, int maxChars, string document)
    {
        if (builder.Length <= maxChars)
        {
            return builder.ToString();
        }

        return builder.ToString(0, maxChars)
            + Environment.NewLine
            + $"> TRUNCATED by the Factory: this {document} document exceeded {maxChars} characters.";
    }

    private static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(none)";
        }

        return text.Length <= maxChars ? text : text[..maxChars] + " …(truncated)";
    }
}

/// <summary>
/// Minimal deterministic line-based unified diff. Exact by construction: every line of both
/// versions appears as context, removal or addition. When the files are too large for the LCS
/// step, the whole file pair is emitted as one removal block plus one addition block — still
/// exact, only less compact.
/// </summary>
public static class UnifiedDiff
{
    private const long MaxLcsCells = 4_000_000;

    public static string Compute(string path, string? baseline, string? final)
    {
        var oldLines = SplitLines(baseline);
        var newLines = SplitLines(final);

        var builder = new StringBuilder();
        builder.AppendLine($"--- a/{path} (baseline)");
        builder.AppendLine($"+++ b/{path} (final)");

        if (oldLines.Length == 0 && newLines.Length == 0)
        {
            return builder.ToString();
        }

        builder.AppendLine($"@@ -1,{oldLines.Length} +1,{newLines.Length} @@");

        if ((long)oldLines.Length * newLines.Length > MaxLcsCells)
        {
            foreach (var line in oldLines)
            {
                builder.Append('-').AppendLine(line);
            }

            foreach (var line in newLines)
            {
                builder.Append('+').AppendLine(line);
            }

            return builder.ToString();
        }

        var table = new int[oldLines.Length + 1, newLines.Length + 1];

        for (var i = oldLines.Length - 1; i >= 0; i--)
        {
            for (var j = newLines.Length - 1; j >= 0; j--)
            {
                table[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var a = 0;
        var b = 0;

        while (a < oldLines.Length && b < newLines.Length)
        {
            if (string.Equals(oldLines[a], newLines[b], StringComparison.Ordinal))
            {
                builder.Append(' ').AppendLine(oldLines[a]);
                a++;
                b++;
            }
            else if (table[a + 1, b] >= table[a, b + 1])
            {
                builder.Append('-').AppendLine(oldLines[a]);
                a++;
            }
            else
            {
                builder.Append('+').AppendLine(newLines[b]);
                b++;
            }
        }

        while (a < oldLines.Length)
        {
            builder.Append('-').AppendLine(oldLines[a]);
            a++;
        }

        while (b < newLines.Length)
        {
            builder.Append('+').AppendLine(newLines[b]);
            b++;
        }

        return builder.ToString();
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
    }
}
