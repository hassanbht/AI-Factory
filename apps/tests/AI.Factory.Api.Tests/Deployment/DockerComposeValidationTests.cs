using System.Diagnostics;
using Xunit;

namespace AI.Factory.Api.Tests.Deployment;

/// <summary>
/// Structural validation of docker-compose.yml via the Docker CLI itself, catching typos and
/// invalid interpolation (e.g. the ${IMAGE_TAG:-latest} / build.args additions from Phase 17.5.1)
/// that a text-only test cannot see.
/// </summary>
public sealed class DockerComposeValidationTests
{
    [Fact]
    public async Task DockerComposeConfig_OnGeneratedComposeFile_ExitsZero()
    {
        // xunit 2.9.2 (this project's installed version) has no Assert.Skip/SkippableFact —
        // that only exists on xunit v3 or the separate Xunit.SkippableFact package, neither of
        // which is referenced here, and adding one is out of scope for this change. So when
        // Docker isn't available on the machine running the tests, detect that and return
        // early: the test trivially passes instead of failing for an unrelated environment reason.
        if (!IsDockerAvailable())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var composeFilePath = Path.Combine(repoRoot, "docker-compose.yml");

        var (exitCode, _, stderr) = await RunProcessAsync(
            "docker",
            $"compose -f \"{composeFilePath}\" config -q",
            repoRoot);

        Assert.True(exitCode == 0, $"`docker compose config -q` failed with exit code {exitCode}: {stderr}");
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception)
        {
            // Docker CLI not installed/not on PATH — treated as "unavailable", not a test failure.
            return false;
        }
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
