using System.Text;
using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Agents;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Validation;

/// <summary>
/// Phase P.1 — Constructs an immutable, provider-neutral <see cref="ReviewBundle"/>
/// containing exact file changes, unified diff, additions, and deletions from the
/// workspace baseline. Independent of any execution runtime or container.
/// </summary>
public sealed class GitReviewBundleBuilder : IReviewBundleBuilder
{
    private const int MaxScannedFileBytes = 512 * 1024;
    private const int MaxDiffBytes = 256 * 1024;

    private static readonly string[] BuildOutputSegments = { "bin", "obj", "node_modules", "TestResults", ".vs", ".git" };

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<GitReviewBundleBuilder> _logger;

    public GitReviewBundleBuilder(
        IProcessRunner processRunner,
        ILogger<GitReviewBundleBuilder> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<ReviewBundle?> BuildBundleAsync(
        string workspacePath,
        IReadOnlyList<string>? changedFiles = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return null;
        }

        var inside = await GitAsync(workspacePath, cancellationToken, "rev-parse", "--is-inside-work-tree");
        if (inside is null || inside.Trim() != "true" || await GitAsync(workspacePath, cancellationToken, "rev-parse", "--verify", "HEAD") is null)
        {
            _logger.LogInformation("ReviewBundleUnavailable Workspace={Workspace} Reason=NoGitBaseline", workspacePath);
            return null;
        }

        var status = await GitAsync(workspacePath, cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (status is null)
        {
            _logger.LogWarning("ReviewBundleUnavailable Workspace={Workspace} Reason=GitStatusFailed", workspacePath);
            return null;
        }

        var changes = new List<ReviewFileChange>();
        var changedPaths = new List<string>();
        var diffBuilder = new StringBuilder();

        var entries = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i].TrimStart('\r', '\n');
            if (entry.Length < 4)
            {
                continue;
            }

            var code = entry[..2];
            var path = entry[3..].TrimEnd('\r', '\n', '/');
            if (code[0] == 'R' || code[0] == 'C')
            {
                i++; // rename source follows
            }

            if (IsBuildOutput(path))
            {
                continue;
            }

            changedPaths.Add(path);

            if (code == "??" || code.Contains('A'))
            {
                var fullPath = Path.Combine(workspacePath, path);
                var content = ReadText(fullPath);
                var fileDiff = BuildAddedFileDiff(path, content);

                changes.Add(new ReviewFileChange(path, ReviewChangeKind.Added, content, fileDiff));
                if (diffBuilder.Length < MaxDiffBytes && fileDiff is not null)
                {
                    diffBuilder.AppendLine(fileDiff);
                }
            }
            else if (code.Contains('D'))
            {
                var deletedContent = await GitAsync(workspacePath, cancellationToken, "show", $"HEAD:{path}");
                var fileDiff = await GitAsync(workspacePath, cancellationToken, "diff", "HEAD", "--", path);

                changes.Add(new ReviewFileChange(path, ReviewChangeKind.Deleted, deletedContent, fileDiff));
                if (diffBuilder.Length < MaxDiffBytes && fileDiff is not null)
                {
                    diffBuilder.AppendLine(fileDiff);
                }
            }
            else
            {
                var fileDiff = await GitAsync(workspacePath, cancellationToken, "diff", "HEAD", "--", path);
                changes.Add(new ReviewFileChange(path, ReviewChangeKind.Modified, null, fileDiff));

                if (diffBuilder.Length < MaxDiffBytes && fileDiff is not null)
                {
                    diffBuilder.AppendLine(fileDiff);
                }
            }
        }

        var unifiedDiff = diffBuilder.ToString();
        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            var gitDiff = await GitAsync(workspacePath, cancellationToken, "diff", "HEAD");
            if (!string.IsNullOrWhiteSpace(gitDiff))
            {
                unifiedDiff = gitDiff.Length > MaxDiffBytes ? gitDiff[..MaxDiffBytes] + "\n...[truncated]" : gitDiff;
            }
        }

        var metadata = new Dictionary<string, string>
        {
            ["Workspace"] = workspacePath,
            ["TotalChangedFiles"] = changedPaths.Count.ToString(),
            ["TimestampUtc"] = DateTime.UtcNow.ToString("O")
        };

        return new ReviewBundle(changedPaths, changes, unifiedDiff, metadata);
    }

    private static string? BuildAddedFileDiff(string path, string? content)
    {
        if (content is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"diff --git a/{path} b/{path}");
        sb.AppendLine("new file mode 100644");
        sb.AppendLine("--- /dev/null");
        sb.AppendLine($"+++ b/{path}");

        var lines = content.Split('\n');
        sb.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
        foreach (var line in lines)
        {
            sb.Append('+').AppendLine(line.TrimEnd('\r'));
        }

        return sb.ToString();
    }

    private static bool IsBuildOutput(string path) =>
        path.Split('/').Any(segment => BuildOutputSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static string? ReadText(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.LinkTarget is not null || info.Length > MaxScannedFileBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(fullPath);
            return Array.IndexOf(bytes, (byte)0) >= 0 ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> GitAsync(string workspacePath, CancellationToken cancellationToken, params string[] arguments)
    {
        var request = new ProcessRunRequest(
            FileName: "git",
            Arguments: arguments,
            Timeout: TimeSpan.FromSeconds(30),
            WorkingDirectory: workspacePath,
            EnvironmentVariables: SafeGitEnvironment());

        var result = await _processRunner.RunAsync(request, cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    private static IReadOnlyDictionary<string, string> SafeGitEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = string.Empty,
            ["GIT_OPTIONAL_LOCKS"] = "0"
        };

        foreach (var name in new[] { "PATH", "TEMP", "TMP", "SYSTEMROOT" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                env[name] = value;
            }
        }

        return env;
    }
}
