using System.Text.Json;
using System.Text.RegularExpressions;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase M — reads what changed in a Project workspace relative to its source baseline. Implemented in
/// Infrastructure (git); Application only consumes the change set.
/// </summary>
public interface IWorkspaceIntegrityInspector
{
    /// <summary>Changes since the workspace baseline, or null when no baseline exists (not a git working tree).</summary>
    Task<IReadOnlyList<WorkspaceFileChange>?> GetChangesAsync(string workspacePath, CancellationToken cancellationToken = default);
}

public enum WorkspaceChangeKind
{
    Added,
    Modified,
    Deleted,
    /// <summary>Present on disk but excluded by .gitignore — still in the workspace.</summary>
    Ignored
}

/// <param name="Path">Workspace-relative, '/'-separated.</param>
/// <param name="BaselineContent">Baseline text, supplied for dependency/project manifests only.</param>
/// <param name="CurrentContent">Current text, supplied for added/modified text files within the scan size limit.</param>
public sealed record WorkspaceFileChange(
    string Path,
    WorkspaceChangeKind Kind,
    string? BaselineContent = null,
    string? CurrentContent = null);

/// <summary>
/// Phase M — the Factory's deterministic workspace-integrity rules (benchmark T1/T2 findings: a stray
/// <c>dotnet-install.sh</c>, a <c>BUILD_SUMMARY.txt</c>, a rewritten test csproj with xunit downgraded and
/// implicit usings removed, an edited solution file, runtime run artifacts left in the repository). An agent
/// may change source and tests; it may not leave runtime state, credentials, logs, installers or reports
/// behind, rewrite build infrastructure, change existing dependency versions or project settings, or write a
/// credential into any file. Violations are a repairable verification failure, reported by name.
/// </summary>
public static class WorkspaceIntegrityPolicy
{
    private static readonly Regex[] ForbiddenPaths =
    {
        // Runtime/agent state, sessions and caches
        Pattern(@"(^|/)\.(claude|codex|hermes|openhands|aider|cursor)(/|$)"),
        Pattern(@"(^|/)artifacts/runs(/|$)"),
        Pattern(@"(^|/)\.aif-[^/]*$"),
        Pattern(@"\.(sqlite3?|db)(-wal|-shm|-journal)?$"),
        // Credentials and provider configuration
        Pattern(@"(^|/)\.env(\.[^/]*)?$"),
        Pattern(@"(^|/)(auth|credentials?)\.json$"),
        Pattern(@"(^|/)\.(netrc|npmrc|pypirc)$"),
        Pattern(@"\.(pem|key|pfx|p12)$"),
        Pattern(@"(^|/)id_(rsa|ed25519|ecdsa)[^/]*$"),
        // Logs, temp files, installers, stray reports
        Pattern(@"\.(log|tmp|swp)$"),
        Pattern(@"(^|/)nohup\.out$"),
        Pattern(@"(^|/)dotnet-install\.(sh|ps1)$"),
        Pattern(@"(^|/)(get-pip\.py|rustup-init[^/]*)$"),
        Pattern(@"(^|/)[^/]*_SUMMARY\.[^/]+$"),
    };

    private static readonly Regex[] ProtectedPaths =
    {
        Pattern(@"\.slnx?$"),
        Pattern(@"(^|/)(global\.json|nuget\.config|Directory\.Build\.(props|targets)|\.gitignore|\.gitattributes)$"),
    };

    private static readonly Regex[] IgnoredButStillForbidden =
    {
        Pattern(@"(^|/)\.(claude|codex|hermes|openhands)(/|$)"),
        Pattern(@"(^|/)\.env(\.[^/]*)?$"),
        Pattern(@"(^|/)(auth|credentials?)\.json$"),
        Pattern(@"(^|/)dotnet-install\.(sh|ps1)$"),
    };

    private static readonly string[] ProtectedProjectProperties =
        { "TargetFramework", "TargetFrameworks", "ImplicitUsings", "Nullable", "LangVersion" };

    private static readonly Regex PackageElement = new(
        @"<(PackageReference|PackageVersion)\b(?<attrs>[^>]*)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IncludeAttribute = new(
        @"\bInclude\s*=\s*""(?<v>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VersionAttribute = new(
        @"\bVersion\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CredentialPattern = new(
        @"\b(sk-(ant|or|proj)-[A-Za-z0-9_\-]{16,}|sk-[A-Za-z0-9]{32,}|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{36,})\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedAdditionsExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".json", ".xml", ".md", ".txt", ".html", ".css", ".js", ".ts",
        ".proto", ".sql", ".razor", ".xaml", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".ico",
        ".editorconfig", ".yml", ".yaml"
    };

    private static readonly string[] AllowedRootDirectories =
    {
        "src/", "tests/", "test/", "docs/", "bench/", "projects/", "apps/", "samples/"
    };

    public static string FormatLossless(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "<empty>";
        }

        var isPrintableAscii = path.All(c => c is >= ' ' and <= '~');
        if (isPrintableAscii)
        {
            return path;
        }

        return $"{path} (code points: {string.Join(" ", path.Select(c => $"U+{(int)c:X4}"))})";
    }

    private static bool IsAllowedNewFile(string path, IEnumerable<string>? explicitAllowlist = null)
    {
        if (explicitAllowlist != null && explicitAllowlist.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // 1. Must be printable ASCII only (no invisible/unicode/control characters like U+F00D)
        if (!path.All(c => c is >= ' ' and <= '~' && c != '\\' && c != ':'))
        {
            return false;
        }

        // 2. Reject hidden files (starts with dot or segment starts with dot)
        if (path.StartsWith('.') || path.Split('/').Any(seg => seg.StartsWith('.')))
        {
            return false;
        }

        // 3. Reject files in root directory (unless explicitly allowlisted)
        if (!path.Contains('/'))
        {
            return false;
        }

        // 4. Reject nested git repositories or gitlinks
        if (path.Contains(".git", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 5. Must reside in an approved directory hierarchy
        if (!AllowedRootDirectories.Any(dir => path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // 6. Must have an allowed extension
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || !AllowedAdditionsExtensions.Contains(ext))
        {
            return false;
        }

        // 7. Must not be a solution or project file unexpectedly added
        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static IReadOnlyList<string> Evaluate(IReadOnlyList<WorkspaceFileChange> changes, IEnumerable<string>? allowedAdditions = null)
    {
        var violations = new List<string>();

        foreach (var change in changes.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            if (change.Kind == WorkspaceChangeKind.Ignored)
            {
                if (IgnoredButStillForbidden.Any(r => r.IsMatch(change.Path)))
                {
                    violations.Add($"runtime state or credential file '{change.Path}' was left in the workspace (git-ignored, still present)");
                }

                continue;
            }

            if (change.Kind != WorkspaceChangeKind.Deleted && ForbiddenPaths.Any(r => r.IsMatch(change.Path)))
            {
                violations.Add($"forbidden file '{change.Path}' ({change.Kind.ToString().ToLowerInvariant()}): runtime state, credentials, logs, installers and reports must not be left in the workspace");
                continue;
            }

            if (change.Kind != WorkspaceChangeKind.Added && ProtectedPaths.Any(r => r.IsMatch(change.Path)))
            {
                violations.Add($"build infrastructure file '{change.Path}' was {change.Kind.ToString().ToLowerInvariant()}; it is out of scope for a WorkItem");
                continue;
            }

            if (change.Kind == WorkspaceChangeKind.Added)
            {
                if (!IsAllowedNewFile(change.Path, allowedAdditions))
                {
                    violations.Add($"unexpected untracked file '{FormatLossless(change.Path)}' (added): additions must be approved source, test or documentation files in recognized project directories");
                    continue;
                }
            }

            if (change.Kind == WorkspaceChangeKind.Modified && change.BaselineContent is not null && change.CurrentContent is not null)
            {
                violations.AddRange(ManifestViolations(change));
            }

            if (change.CurrentContent is not null && CredentialPattern.IsMatch(change.CurrentContent))
            {
                violations.Add($"'{change.Path}' contains what looks like a credential");
            }
        }

        return violations;
    }

    private static IEnumerable<string> ManifestViolations(WorkspaceFileChange change)
    {
        if (change.Path.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || change.Path.EndsWith(".props", StringComparison.OrdinalIgnoreCase))
        {
            var before = Packages(change.BaselineContent!);
            var after = Packages(change.CurrentContent!);

            foreach (var (id, version) in before)
            {
                if (!after.TryGetValue(id, out var now))
                {
                    yield return $"'{change.Path}': existing dependency '{id}' was removed";
                }
                else if (!string.Equals(version, now, StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"'{change.Path}': dependency '{id}' version changed {version} -> {now}";
                }
            }

            foreach (var property in ProtectedProjectProperties)
            {
                var was = Property(change.BaselineContent!, property);
                var now = Property(change.CurrentContent!, property);
                if (was is not null && !string.Equals(was, now, StringComparison.Ordinal))
                {
                    yield return $"'{change.Path}': project setting <{property}> changed '{was}' -> '{now ?? "(removed)"}'";
                }
            }
        }
        else if (change.Path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
        {
            var before = NpmDependencies(change.BaselineContent!);
            var after = NpmDependencies(change.CurrentContent!);
            foreach (var (id, version) in before)
            {
                if (!after.TryGetValue(id, out var now) || now != version)
                {
                    yield return $"'{change.Path}': dependency '{id}' changed {version} -> {now ?? "(removed)"}";
                }
            }
        }
    }

    private static Dictionary<string, string> Packages(string xml)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match element in PackageElement.Matches(xml))
        {
            var attributes = element.Groups["attrs"].Value;
            if (IncludeAttribute.Match(attributes) is { Success: true } include)
            {
                packages.TryAdd(include.Groups["v"].Value, VersionAttribute.Match(attributes).Groups["v"].Value);
            }
        }

        return packages;
    }

    private static string? Property(string xml, string name)
    {
        var match = Regex.Match(xml, $@"<{name}>\s*([^<]*?)\s*</{name}>");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static Dictionary<string, string> NpmDependencies(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies" })
            {
                if (document.RootElement.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
                {
                    foreach (var dep in deps.EnumerateObject())
                    {
                        result[$"{section}:{dep.Name}"] = dep.Value.ToString();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable manifest: the build/test gate reports it.
        }

        return result;
    }

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
