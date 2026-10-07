using System.Text;
using AI.Factory.Application.Orchestration;

namespace AI.Factory.Application.Abstractions.Execution;

/// <summary>
/// Phase P.1 — writes <see cref="ExecutionWorkspace.SeedFiles"/> into the workspace directory the
/// adapter resolved (Factory-resolved workspace or the runtime's own scratch). The payload reaches
/// the runtime through the workspace instead of a command line, and only ever through the directory
/// the runtime itself will use — never by mounting another workspace and never from host state.
///
/// Path safety: seed paths are Factory-generated relative paths. Rooted paths, "..", invalid
/// characters and oversized payloads are rejected with a <see cref="SecurityViolationException"/>
/// before anything is written.
/// </summary>
public static class ExecutionWorkspaceSeed
{
    /// <summary>Upper bound for a single seeded file.</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>Upper bound for all seeded files of one execution combined.</summary>
    public const int MaxTotalBytes = 8 * 1024 * 1024;

    /// <summary>Writes every seed file; no-op for null/empty. Returns the relative paths written.</summary>
    public static IReadOnlyList<string> Write(string workspacePath, IReadOnlyDictionary<string, string>? seedFiles)
    {
        if (seedFiles is null || seedFiles.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new SecurityViolationException("Seed files require a resolved workspace directory.");
        }

        var written = new List<string>(seedFiles.Count);
        var totalBytes = 0;

        foreach (var (relativePath, content) in seedFiles)
        {
            ValidateRelativePath(relativePath);

            var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);

            if (bytes.Length > MaxFileBytes)
            {
                throw new SecurityViolationException(
                    $"Seed file '{relativePath}' exceeds the {MaxFileBytes} byte payload limit.");
            }

            totalBytes += bytes.Length;

            if (totalBytes > MaxTotalBytes)
            {
                throw new SecurityViolationException(
                    $"Seed files exceed the {MaxTotalBytes} byte combined payload limit.");
            }

            var fullPath = Path.GetFullPath(Path.Combine(workspacePath, relativePath));

            if (!IsWithin(workspacePath, fullPath))
            {
                throw new SecurityViolationException(
                    $"Seed file '{relativePath}' resolves outside the workspace directory.");
            }

            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(fullPath, bytes);
            written.Add(relativePath);
        }

        return written;
    }

    private static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new SecurityViolationException("Seed file path must not be empty.");
        }

        if (Path.IsPathRooted(relativePath)
            || relativePath.StartsWith('/') || relativePath.StartsWith('\\')
            || relativePath.Contains("..", StringComparison.Ordinal)
            || relativePath.Contains(':'))
        {
            throw new SecurityViolationException(
                $"Seed file path '{relativePath}' must be a plain relative path.");
        }

        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                throw new SecurityViolationException(
                    $"Seed file path '{relativePath}' contains an empty or current-directory segment.");
            }

            if (segment.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            {
                throw new SecurityViolationException(
                    $"Seed file path '{relativePath}' contains invalid characters.");
            }
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);

        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                normalizedCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }
}
