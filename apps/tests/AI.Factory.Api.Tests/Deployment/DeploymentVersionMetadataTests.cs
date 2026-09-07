using AI.Factory.Application.Configuration;
using AI.Factory.Infrastructure.Diagnostics;
using Xunit;

namespace AI.Factory.Api.Tests.Deployment;

/// <summary>
/// These tests read and write process-wide environment variables (BUILD_COMMIT, BUILD_BRANCH,
/// BUILD_NUMBER, BUILD_TIMESTAMP_UTC), so they must never run in parallel with each other or
/// with any other test touching the same variables.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeploymentVersionMetadataCollection
{
    public const string Name = "DeploymentVersionMetadata";
}

[Collection(DeploymentVersionMetadataCollection.Name)]
public sealed class DeploymentVersionMetadataTests
{
    private static readonly string[] EnvVarNames =
    {
        "BUILD_COMMIT", "GIT_COMMIT",
        "BUILD_BRANCH", "GIT_BRANCH",
        "BUILD_NUMBER", "GITHUB_RUN_ID",
        "BUILD_TIMESTAMP", "BUILD_TIMESTAMP_UTC",
    };

    [Fact]
    public void GetSnapshot_WithNoBuildEnvVars_ReturnsNullVersionMetadata()
    {
        var previous = CaptureAndClearEnvVars();
        try
        {
            var service = new DeploymentDiagnosticsService(new DatabaseOptions
            {
                Provider = "Sqlite",
                ConnectionString = "Data Source=:memory:",
            });

            var snapshot = service.GetSnapshot();

            Assert.Null(snapshot.GitCommit);
            Assert.Null(snapshot.GitBranch);
            Assert.Null(snapshot.BuildNumber);
            Assert.Null(snapshot.BuildTimestamp);
        }
        finally
        {
            RestoreEnvVars(previous);
        }
    }

    [Fact]
    public void GetSnapshot_WithBuildEnvVarsSet_ReflectsExactValues()
    {
        var previous = CaptureAndClearEnvVars();
        try
        {
            Environment.SetEnvironmentVariable("BUILD_COMMIT", "abc1234");
            Environment.SetEnvironmentVariable("BUILD_BRANCH", "release/1.2.3");
            Environment.SetEnvironmentVariable("BUILD_NUMBER", "4242");
            Environment.SetEnvironmentVariable("BUILD_TIMESTAMP_UTC", "2026-01-01T00:00:00Z");

            var service = new DeploymentDiagnosticsService(new DatabaseOptions
            {
                Provider = "Sqlite",
                ConnectionString = "Data Source=:memory:",
            });

            var snapshot = service.GetSnapshot();

            Assert.Equal("abc1234", snapshot.GitCommit);
            Assert.Equal("release/1.2.3", snapshot.GitBranch);
            Assert.Equal("4242", snapshot.BuildNumber);
            Assert.Equal("2026-01-01T00:00:00Z", snapshot.BuildTimestamp);
        }
        finally
        {
            RestoreEnvVars(previous);
        }
    }

    private static Dictionary<string, string?> CaptureAndClearEnvVars()
    {
        var previous = new Dictionary<string, string?>();
        foreach (var name in EnvVarNames)
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        return previous;
    }

    private static void RestoreEnvVars(Dictionary<string, string?> previous)
    {
        foreach (var (name, value) in previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
