using AI.Factory.Infrastructure.Agents;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Isolated integration test: exercises the real `docker` binary end-to-end
/// through SystemProcessRunner. Deliberately limited to `docker --version` —
/// no image pull, no network, no Hermes/API keys involved — so it stays
/// fast and safe to run whenever Docker is installed, without requiring the
/// actual Hermes agent image or a running daemon.
/// </summary>
public sealed class HermesDockerIntegrationTests
{
    [Fact]
    public async Task Docker_Cli_Is_Reachable_Through_SystemProcessRunner()
    {
        var runner = new SystemProcessRunner();

        var result = await runner.RunAsync(
            new ProcessRunRequest("docker", new[] { "--version" }, TimeSpan.FromSeconds(10)));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Docker version", result.StandardOutput);
    }
}
