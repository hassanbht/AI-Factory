namespace AI.Factory.Infrastructure.Validation;

/// <summary>
/// Phase 18.29.1 — decides WHAT `dotnet build` / `dotnet test` should be pointed at
/// when the verification gate hands them a workspace directory.
///
/// The gate passes the Project's workspace root (VerificationGate resolves it via
/// IWorkspaceResolver, so "agent workspace == verification workspace" stays true by
/// construction). `dotnet build &lt;dir&gt;` only looks for a project or solution
/// directly IN that directory — it does not recurse. A generated project one level
/// down, which is the layout an agent naturally produces and the layout this very
/// repository uses (apps/AI.Factory.slnx), therefore failed every verification with
/// "MSB1003: Specify a project or solution file", and the repair loop then spent its
/// bounded attempts on a defect that was in the framing rather than in the code.
///
/// Deliberately conservative:
/// * A file path is returned untouched.
/// * A directory that already contains a project/solution is returned UNTOUCHED, so
///   MSBuild resolves it exactly as before — this is a pure fallback, not a change
///   to the existing, working path.
/// * A nested target is only chosen when it is UNAMBIGUOUS: a solution wins over
///   projects, and the shallowest candidate wins. If the shallowest depth still
///   holds more than one candidate, the directory is returned unchanged so MSBuild
///   reports its own error — guessing which of several projects the Factory meant
///   to verify would be worse than failing loudly.
/// </summary>
public static class MsBuildTargetResolver
{
    private static readonly string[] SolutionExtensions = { ".slnx", ".sln" };

    private static readonly string[] ProjectExtensions = { ".csproj", ".fsproj", ".vbproj" };

    /// <summary>Same exclusions FileSystemArtifactRecorder uses — build output is never a build target.</summary>
    private static readonly string[] ExcludedDirectoryNames = { "bin", "obj", "node_modules", ".git", ".vs" };

    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || File.Exists(path) || !Directory.Exists(path))
        {
            return path;
        }

        // Unchanged behavior whenever MSBuild can already resolve the directory itself.
        if (HasTargetDirectlyIn(path))
        {
            return path;
        }

        return FindUnambiguousNestedTarget(path, SolutionExtensions)
            ?? FindUnambiguousNestedTarget(path, ProjectExtensions)
            ?? path;
    }

    private static bool HasTargetDirectlyIn(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(IsBuildTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? FindUnambiguousNestedTarget(string root, string[] extensions)
    {
        List<string> candidates;

        try
        {
            candidates = Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => HasExtension(file, extensions))
                .Where(file => !IsUnderExcludedDirectory(root, file))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var shallowest = candidates
            .GroupBy(file => DepthBelow(root, file))
            .OrderBy(group => group.Key)
            .First()
            .ToList();

        // Ambiguous — let MSBuild say so rather than silently verifying one of them.
        return shallowest.Count == 1 ? shallowest[0] : null;
    }

    private static bool IsBuildTarget(string file) =>
        HasExtension(file, SolutionExtensions) || HasExtension(file, ProjectExtensions);

    private static bool HasExtension(string file, string[] extensions) =>
        extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    private static int DepthBelow(string root, string file) =>
        Path.GetRelativePath(root, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Length;

    private static bool IsUnderExcludedDirectory(string root, string file)
    {
        var relativeSegments = Path.GetRelativePath(root, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // The file name itself is not a directory segment.
        return relativeSegments
            .Take(relativeSegments.Length - 1)
            .Any(segment => ExcludedDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }
}
