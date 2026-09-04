using System.Diagnostics;
using Xunit;

namespace AI.Factory.Api.Tests.Deployment;

[Trait("Category", "ContainerIntegration")]
public sealed class MigrationStartupFaultInjectionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void MigrationFailureOverride_IsValidComposeConfiguration()
    {
        var baseCompose = Path.Combine(RepoRoot, "docker-compose.yml");
        var overrideCompose = Path.Combine(RepoRoot, "docker-compose.test-migration-failure.yml");

        Assert.True(File.Exists(baseCompose), $"docker-compose.yml not found at {baseCompose}");
        Assert.True(File.Exists(overrideCompose), $"docker-compose.test-migration-failure.yml not found at {overrideCompose}");

        var overrideContent = File.ReadAllText(overrideCompose);
        Assert.Contains("--migrate-only", overrideContent);
        Assert.Contains("--simulate-failure", overrideContent);
    }

    [Fact]
    public async Task MigratorCommandLine_WithSimulateFailure_ExitsWithCode1AndLogsFailure()
    {
        var apiDll = typeof(Program).Assembly.Location;
        Assert.True(File.Exists(apiDll), $"API assembly not found at {apiDll}");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{apiDll}\" --migrate-only --simulate-failure",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combinedOutput = stdout + "\n" + stderr;

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("Simulated migration failure triggered for verification. Exiting with code 1.", combinedOutput);
    }
}
