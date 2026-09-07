using System.Reflection;
using System.Runtime.InteropServices;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;

namespace AI.Factory.Infrastructure.Diagnostics;

/// <summary>
/// Collects runtime deployment and container diagnostics without exposing any secrets,
/// connection strings, passwords, or internal filesystem paths.
/// </summary>
public sealed class DeploymentDiagnosticsService : IDeploymentDiagnosticsService
{
    private readonly DatabaseOptions _databaseOptions;
    private readonly string _environment;
    private readonly string _applicationName;
    private readonly string _version;
    private readonly string _informationalVersion;
    private readonly string _frameworkDescription;
    private readonly string _osDescription;
    private readonly string _processArchitecture;
    private readonly bool _isContainerized;
    private readonly DateTime _startedAtUtc;
    private readonly string? _gitCommit;
    private readonly string? _buildTimestamp;
    private readonly string? _gitBranch;
    private readonly string? _buildNumber;

    public DeploymentDiagnosticsService(DatabaseOptions databaseOptions)
    {
        _databaseOptions = databaseOptions;

        var entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        _applicationName = entryAssembly.GetName().Name ?? "AI.Factory";

        var asmVersion = entryAssembly.GetName().Version;
        _version = asmVersion is not null ? asmVersion.ToString() : "1.0.0.0";

        var infoAttr = entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        _informationalVersion = infoAttr?.InformationalVersion ?? _version;

        _environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                       ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                       ?? "Production";

        _frameworkDescription = RuntimeInformation.FrameworkDescription;
        _osDescription = RuntimeInformation.OSDescription;
        _processArchitecture = RuntimeInformation.ProcessArchitecture.ToString();

        _isContainerized = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
                           || File.Exists("/.dockerenv");

        _startedAtUtc = DateTime.UtcNow;

        _gitCommit = Environment.GetEnvironmentVariable("BUILD_COMMIT")
                     ?? Environment.GetEnvironmentVariable("GIT_COMMIT");

        _buildTimestamp = Environment.GetEnvironmentVariable("BUILD_TIMESTAMP")
                           ?? Environment.GetEnvironmentVariable("BUILD_TIMESTAMP_UTC");

        _gitBranch = Environment.GetEnvironmentVariable("BUILD_BRANCH")
                     ?? Environment.GetEnvironmentVariable("GIT_BRANCH");

        _buildNumber = Environment.GetEnvironmentVariable("BUILD_NUMBER")
                       ?? Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
    }

    public DeploymentDiagnosticsSnapshot GetSnapshot()
    {
        var uptime = DateTime.UtcNow - _startedAtUtc;

        return new DeploymentDiagnosticsSnapshot(
            ApplicationName: _applicationName,
            Environment: _environment,
            Version: _version,
            InformationalVersion: _informationalVersion,
            FrameworkDescription: _frameworkDescription,
            OSDescription: _osDescription,
            ProcessArchitecture: _processArchitecture,
            IsContainerized: _isContainerized,
            StartedAtUtc: _startedAtUtc,
            Uptime: uptime,
            DatabaseProvider: _databaseOptions.Provider,
            GitCommit: _gitCommit,
            BuildTimestamp: _buildTimestamp,
            GitBranch: _gitBranch,
            BuildNumber: _buildNumber);
    }
}
