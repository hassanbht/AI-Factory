using System.Diagnostics;
using Xunit;
using AI.Factory.TestSupport;

namespace AI.Factory.Api.Tests.Deployment;

/// <summary>
/// Structural validation of docker-compose.yml via the Docker CLI itself, catching typos and
/// invalid interpolation (e.g. the ${IMAGE_TAG:-latest} / build.args additions from Phase 17.5.1)
/// that a text-only test cannot see.
/// </summary>
public sealed class DockerComposeValidationTests
{
    [DockerFact(RequiresDaemon = false)]
    public async Task DockerComposeConfig_OnGeneratedComposeFile_ExitsZero()
    {
        var repoRoot = FindRepoRoot();
        var composeFilePath = Path.Combine(repoRoot, "docker-compose.yml");

        var (exitCode, _, stderr) = await RunProcessAsync(
            "docker",
            $"compose -f \"{composeFilePath}\" config -q",
            repoRoot);

        Assert.True(exitCode == 0, $"`docker compose config -q` failed with exit code {exitCode}: {stderr}");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "apps", "AI.Factory.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repo root (apps/AI.Factory.slnx) walking up from {AppContext.BaseDirectory}.");
    }
}
