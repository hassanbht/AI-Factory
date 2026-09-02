namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Enforces that every path an agent execution touches stays inside its
/// configured workspace root — never rely on prompt instructions alone
/// (an agent's output is untrusted input) to keep file operations bounded.
/// </summary>
public static class WorkspacePathGuard
{
    /// <summary>
    /// Resolves both paths fully (collapsing "..", symlinks, and relative
    /// segments via the OS) before comparing, so "../" traversal and a
    /// symlink that resolves outside the root are both caught — not just
    /// a naive string prefix check on the unresolved input.
    /// </summary>
    public static string EnsureWithinRoot(string root, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new SecurityViolationException("Workspace root is not configured.");
        }

        var resolvedRoot = ResolveFully(root);
        var resolvedCandidate = ResolveFully(Path.IsPathRooted(candidatePath)
            ? candidatePath
            : Path.Combine(resolvedRoot, candidatePath));

        var rootWithSeparator = resolvedRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var isWithinRoot =
            string.Equals(resolvedCandidate, resolvedRoot, StringComparison.OrdinalIgnoreCase)
            || resolvedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);

        if (!isWithinRoot)
        {
            throw new SecurityViolationException(
                $"Path '{candidatePath}' resolves outside the configured workspace root.");
        }

        return resolvedCandidate;
    }

    /// <summary>
    /// GetFullPath collapses ".."/"." segments; resolving the deepest
    /// existing ancestor's real path (via DirectoryInfo.ResolveLinkTarget
    /// where the OS supports it) catches a symlink planted inside an
    /// otherwise-legitimate path from redirecting outside the root.
    /// </summary>
    private static string ResolveFully(string path)
    {
        var fullPath = Path.GetFullPath(path);

        var probe = new DirectoryInfo(fullPath);

        while (probe is not null && !probe.Exists)
        {
            probe = probe.Parent;
        }

        if (probe is null)
        {
            return fullPath;
        }

        var resolvedAncestor = probe.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? probe.FullName;
        var remainder = Path.GetRelativePath(probe.FullName, fullPath);

        return remainder == "."
            ? resolvedAncestor
            : Path.GetFullPath(Path.Combine(resolvedAncestor, remainder));
    }
}
