using System.Diagnostics.CodeAnalysis;

namespace AI.Factory.Infrastructure.Configuration;

/// <summary>
/// Cross-platform resolution of configured filesystem paths.
///
/// The container deployment configures absolute Linux paths
/// (<c>/var/lib/ai-factory/...</c>), which are correct inside the image and
/// meaningless anywhere else. The same published output also runs on Windows
/// (IIS or a Windows Service), where such a path is rooted but not fully
/// qualified: it silently resolves against the current drive
/// (<c>C:\var\lib\ai-factory\...</c>), a directory the hosting identity
/// generally cannot create. That is how a Windows deployment reaches
/// "SQLite Error 14: unable to open database file" only after startup logging
/// has already reported a healthy configuration.
///
/// Two mechanisms keep both deployments honest:
/// <list type="bullet">
///   <item>an OS-specific configuration overlay — see
///   <see cref="PlatformConfigurationExtensions.AddPlatformSpecificOverlay"/> —
///   supplies the platform's own paths;</item>
///   <item>every configured path is environment-variable expanded and made
///   absolute against a known base directory (the content root) instead of the
///   ambient current directory, which under IIS is not the application
///   directory.</item>
/// </list>
/// </summary>
public static class DeploymentPaths
{
    /// <summary>
    /// Platform token used in <c>appsettings.{Environment}.{Platform}.json</c>.
    /// Matches the RID-style names operators already recognize.
    /// </summary>
    public static string PlatformName =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "OSX"
        : "Linux";

    /// <summary>
    /// Expands <c>%VARIABLE%</c> references so a deployment can point at
    /// <c>%ProgramData%</c> (or any operator-defined variable) without
    /// hard-coding a drive letter. Null/blank values pass through unchanged so
    /// callers keep their own "not configured" semantics.
    /// </summary>
    [return: NotNullIfNotNull(nameof(configuredPath))]
    public static string? Expand(string? configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : Environment.ExpandEnvironmentVariables(configuredPath);

    /// <summary>
    /// Expands <paramref name="configuredPath"/> and returns it fully qualified:
    /// a relative value resolves against <paramref name="basePath"/> (the content
    /// root), never against the process current directory.
    /// </summary>
    public static string ResolveAgainst(string basePath, string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        var expanded = Expand(configuredPath)!;

        return Path.GetFullPath(
            Path.IsPathRooted(expanded) ? expanded : Path.Combine(basePath, expanded));
    }

    /// <summary>
    /// True when a configured value is an absolute POSIX path while the process
    /// runs on Windows — i.e. a container path that was never overridden for this
    /// deployment. Used to turn an opaque I/O failure into an actionable message.
    /// </summary>
    public static bool IsContainerPathOnWindows(string? configuredPath) =>
        OperatingSystem.IsWindows()
        && !string.IsNullOrWhiteSpace(configuredPath)
        && configuredPath[0] is '/' or '\\'
        && !configuredPath.StartsWith(@"\\", StringComparison.Ordinal);
}
