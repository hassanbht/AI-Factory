namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Immutable snapshot of deployment, container, and runtime environment metadata.
/// Guaranteed to never expose secrets, passwords, connection strings, or internal file paths.
/// </summary>
public sealed record DeploymentDiagnosticsSnapshot(
    string ApplicationName,
    string Environment,
    string Version,
    string InformationalVersion,
    string FrameworkDescription,
    string OSDescription,
    string ProcessArchitecture,
    bool IsContainerized,
    DateTime StartedAtUtc,
    TimeSpan Uptime,
    string DatabaseProvider,
    string? GitCommit = null,
    string? BuildTimestamp = null);

/// <summary>
/// Collects runtime deployment and container diagnostics.
/// </summary>
public interface IDeploymentDiagnosticsService
{
    DeploymentDiagnosticsSnapshot GetSnapshot();
}
