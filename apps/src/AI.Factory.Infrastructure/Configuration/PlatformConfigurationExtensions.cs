using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;

namespace AI.Factory.Infrastructure.Configuration;

/// <summary>
/// Adds an optional operating-system specific configuration overlay so one
/// published output can serve both the containerized (Linux) deployment and a
/// Windows host deployment (IIS / Windows Service) without a second build.
///
/// The overlay is ordinary configuration — no path rewriting, no guessing: a
/// Windows deployment declares its own paths in
/// <c>appsettings.{Environment}.Windows.json</c>, which ships next to
/// <c>appsettings.{Environment}.json</c> and is simply absent on Linux.
/// </summary>
public static class PlatformConfigurationExtensions
{
    /// <summary>
    /// Inserts <c>appsettings.{EnvironmentName}.{Platform}.json</c> directly after
    /// the host's own <c>appsettings*.json</c> sources: it therefore overrides the
    /// environment file, and is itself still overridden by environment variables
    /// and command-line arguments (which is what docker-compose and the release
    /// pipeline rely on).
    /// </summary>
    public static IConfigurationBuilder AddPlatformSpecificOverlay(
        this IConfigurationBuilder configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var source = new JsonConfigurationSource
        {
            FileProvider = environment.ContentRootFileProvider,
            Path = $"appsettings.{environment.EnvironmentName}.{DeploymentPaths.PlatformName}.json",
            Optional = true,
            ReloadOnChange = true
        };

        configuration.Sources.Insert(IndexAfterAppSettingsSources(configuration.Sources), source);

        return configuration;
    }

    /// <summary>
    /// Position right after the last <c>appsettings*.json</c> source. When a host
    /// has none (only tests construct such a builder), the overlay goes first so it
    /// can never outrank environment variables.
    /// </summary>
    private static int IndexAfterAppSettingsSources(IList<IConfigurationSource> sources)
    {
        for (var i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is JsonConfigurationSource json &&
                Path.GetFileName(json.Path ?? string.Empty)
                    .StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }
}
