using System.Text;
using AI.Factory.Application.Orchestration;
using AI.Factory.Infrastructure.Agents;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Validation;

/// <summary>
/// Phase M — <see cref="IWorkspaceIntegrityInspector"/> over git: the Project workspace is a git working
/// tree initialised from its source, so `git status` against HEAD is the exact set of changes the agents
/// made. Untracked and git-ignored files are included (a git-ignored <c>.env</c> is still in the workspace);
/// build output folders are not. Read-only: it never stages, commits, cleans or reverts anything.
/// </summary>
public sealed class GitWorkspaceIntegrityInspector : IWorkspaceIntegrityInspector
{
    private const int MaxScannedFileBytes = 512 * 1024;

    private static readonly string[] BuildOutputSegments = { "bin", "obj", "node_modules", "TestResults", ".vs", ".git" };

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<GitWorkspaceIntegrityInspector> _logger;

    public GitWorkspaceIntegrityInspector(IProcessRunner processRunner, ILogger<GitWorkspaceIntegrityInspector> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkspaceFileChange>?> GetChangesAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var inside = await GitAsync(workspacePath, cancellationToken, "rev-parse", "--is-inside-work-tree");
        if (inside is null || inside.Trim() != "true" || await GitAsync(workspacePath, cancellationToken, "rev-parse", "--verify", "HEAD") is null)
        {
            _logger.LogInformation("WorkspaceIntegrityUnavailable Workspace={Workspace} Reason=NoGitBaseline", workspacePath);
            return null;
        }

        // A baseline exists, so "could not inspect" must never read as "clean": the gate turns this into an
        // InfrastructureError instead of letting build/test pass unchecked.
        var status = await GitAsync(workspacePath, cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching")
            ?? throw new InvalidOperationException(
                $"Workspace integrity could not be inspected: 'git status' failed or timed out in '{workspacePath}'.");

        var changes = new List<WorkspaceFileChange>();
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
                i++; // the rename source follows as its own NUL-terminated field
            }

            if (IsBuildOutput(path))
            {
                continue;
            }

            var kind = code switch
            {
                "??" => WorkspaceChangeKind.Added,
                "!!" => WorkspaceChangeKind.Ignored,
                _ when code.Contains('D') => WorkspaceChangeKind.Deleted,
                _ when code.Contains('A') => WorkspaceChangeKind.Added,
                _ => WorkspaceChangeKind.Modified
            };

            var current = kind is WorkspaceChangeKind.Added or WorkspaceChangeKind.Modified
                ? ReadText(Path.Combine(workspacePath, path))
                : null;
            var baseline = kind == WorkspaceChangeKind.Modified && IsManifest(path)
                ? await GitAsync(workspacePath, cancellationToken, "show", $"HEAD:{path}")
                : null;

            changes.Add(new WorkspaceFileChange(path, kind, baseline, current));
        }

        return changes;
    }

    private static bool IsBuildOutput(string path) =>
        path.Split('/').Any(segment => BuildOutputSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static bool IsManifest(string path) =>
        path.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Text content for scanning; null for directories, binaries, links and oversized files.</summary>
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The workspace (including .git) is agent-writable, so git runs hardened: no fsmonitor/hook/pager
    /// command from repository config can execute, no optional index locks are taken, and the child gets a
    /// minimal environment (path, temp, home) — never the Worker's secrets. safe.directory is set
    /// to this one workspace (it may be owned by a container user).
    /// </summary>
    private async Task<string?> GitAsync(string workspacePath, CancellationToken cancellationToken, params string[] arguments)
    {
        var hardened = new[]
        {
            "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=", "-c", "core.pager=cat",
            "-c", $"safe.directory={workspacePath.Replace('\\', '/')}", "-C", workspacePath
        };
        // System/global config stay readable on purpose: they carry core.autocrlf (Git for Windows sets it
        // system-wide), and without it a CRLF checkout compares as "modified" whenever git's stat cache cannot
        // vouch for a file. The command-running settings they could contain are overridden by -c above.
        var environment = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
        foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                environment[name] = value;
            }
        }

        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessRunRequest("git", hardened.Concat(arguments).ToArray(), TimeSpan.FromSeconds(120), EnvironmentVariables: environment),
                cancellationToken);

            if (result.ExitCode != 0 || result.TimedOut)
            {
                _logger.LogWarning(
                    "WorkspaceIntegrityGitFailed Workspace={Workspace} Command={Command} ExitCode={ExitCode} TimedOut={TimedOut}",
                    workspacePath, arguments[0], result.ExitCode, result.TimedOut);
                return null;
            }

            return result.StandardOutput;
        }
        catch (ProcessLaunchException ex)
        {
            _logger.LogWarning(ex, "WorkspaceIntegrityUnavailable Workspace={Workspace} Reason=GitNotAvailable", workspacePath);
            return null;
        }
    }
}
