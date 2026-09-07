namespace AI.Factory.Application.Versioning;

/// <summary>
/// Pure, dependency-free logic for validating a released Git tag against the repository's
/// <c>VERSION</c> file content. Used by <c>.github/workflows/release.yml</c>'s tag-validation
/// step (shell) and unit-tested here directly, since the workflow YAML itself cannot be
/// exercised by <c>dotnet test</c>. Both must apply the same normalization rule; if either one
/// changes, update the other.
/// </summary>
public static class ReleaseTagValidator
{
    /// <summary>
    /// Normalizes a Git tag by stripping a single leading "v" if present. Nothing else is
    /// stripped — in particular a pre-release suffix (e.g. "v0.1.0-rc1") is preserved as-is, so
    /// it will only equal a VERSION file value that carries the identical suffix. This is
    /// deliberate: a pre-release tag must never be silently treated as matching the release
    /// version unless the VERSION file explicitly says so.
    /// </summary>
    public static string NormalizeTag(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var trimmed = tag.Trim();
        return trimmed.StartsWith('v') ? trimmed[1..] : trimmed;
    }

    /// <summary>
    /// Returns true only if the normalized tag exactly equals the (trimmed) VERSION file
    /// content. Used to fail the release workflow before any build/publish work when the two
    /// diverge (e.g. VERSION=0.1.0, tag=v0.2.0).
    /// </summary>
    public static bool TagMatchesVersion(string tag, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var normalizedTag = NormalizeTag(tag);
        var normalizedVersion = version.Trim();
        return string.Equals(normalizedTag, normalizedVersion, StringComparison.Ordinal);
    }
}
