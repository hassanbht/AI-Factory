using Xunit;

namespace AI.Factory.Api.Tests.Deployment;

/// <summary>
/// Regression guard: the migration-failure simulation switch (used only by
/// docker-compose.test-migration-failure.yml and its AI_FACTORY_MIGRATE_FAIL_TEST /
/// --simulate-failure trigger — see Program.cs) must never leak into files that ship
/// to production.
/// </summary>
public sealed class ProductionConfigurationSafetyTests
{
    private const string ForbiddenEnvVar = "AI_FACTORY_MIGRATE_FAIL_TEST";
    private const string ForbiddenFlag = "--simulate-failure";

    [Fact]
    public void DockerComposeYml_DoesNotContainMigrationFailureSimulationSwitch()
    {
        var path = Path.Combine(FindRepoRoot(), "docker-compose.yml");
        var content = File.ReadAllText(path);

        AssertDoesNotContainForbiddenTokens(content, path);
    }

    [Fact]
    public void AppSettingsProduction_DoesNotContainMigrationFailureSimulationSwitch()
    {
        var path = Path.Combine(FindRepoRoot(), "apps", "src", "AI.Factory.Api", "appsettings.Production.json");
        var content = File.ReadAllText(path);

        AssertDoesNotContainForbiddenTokens(content, path);
    }

    private static void AssertDoesNotContainForbiddenTokens(string content, string path)
    {
        Assert.False(
            content.Contains(ForbiddenEnvVar, StringComparison.OrdinalIgnoreCase),
            $"{path} must never reference {ForbiddenEnvVar} (test-only migration failure switch).");

        Assert.False(
            content.Contains(ForbiddenFlag, StringComparison.OrdinalIgnoreCase),
            $"{path} must never reference {ForbiddenFlag} (test-only migration failure switch).");
    }

    /// <summary>
    /// Walks up from the test assembly's base directory until it finds the repo root
    /// (the directory that contains "apps/AI.Factory.slnx").
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "AI.Factory.slnx");
            if (File.Exists(candidate))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repo root (apps/AI.Factory.slnx) walking up from {AppContext.BaseDirectory}.");
    }
}
