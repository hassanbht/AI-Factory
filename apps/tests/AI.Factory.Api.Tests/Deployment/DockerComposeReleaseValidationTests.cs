using System.Diagnostics;
using Xunit;

namespace AI.Factory.Api.Tests.Deployment;

/// <summary>
/// Validates docker-compose.release.yml (phase 17.5.2, Workstream E) via the Docker CLI itself:
/// it must fail loudly when AI_FACTORY_IMAGE_TAG is unset (no silent "latest" fallback for
/// pinned production deployment) and must resolve cleanly once both required env vars are set.
/// Mirrors the Docker-availability skip pattern in <see cref="DockerComposeValidationTests"/>.
/// </summary>
public sealed class DockerComposeReleaseValidationTests
{
    [Fact]
    public async Task DockerComposeConfig_WithoutDirectImages_FailsLoudly()
    {
        if (!IsDockerAvailable())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var (exitCode, _, stderr) = await RunProcessAsync(
            "docker",
            BuildConfigArgs(repoRoot),
            repoRoot,
            env: new Dictionary<string, string?>
            {
                ["AI_FACTORY_API_IMAGE"] = null,
                ["AI_FACTORY_WORKER_IMAGE"] = null,
            });

        Assert.NotEqual(0, exitCode);
        Assert.True(
            stderr.Contains("AI_FACTORY_API_IMAGE must be set", StringComparison.Ordinal) ||
            stderr.Contains("AI_FACTORY_WORKER_IMAGE must be set", StringComparison.Ordinal),
            $"Expected stderr to contain missing image env error, got: {stderr}");
    }

    [Fact]
    public async Task DockerComposeConfig_WithoutApiImage_FailsLoudly()
    {
        if (!IsDockerAvailable())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var (exitCode, _, stderr) = await RunProcessAsync(
            "docker",
            BuildConfigArgs(repoRoot),
            repoRoot,
            env: new Dictionary<string, string?>
            {
                ["AI_FACTORY_API_IMAGE"] = null,
                ["AI_FACTORY_WORKER_IMAGE"] = "ghcr.io/example-owner/example-repo/worker@sha256:2222222222222222222222222222222222222222222222222222222222222222",
            });

        Assert.NotEqual(0, exitCode);
        Assert.Contains("AI_FACTORY_API_IMAGE must be set", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DockerComposeConfig_WithImmutableDirectImagesSet_ExitsZero()
    {
        if (!IsDockerAvailable())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var (exitCode, _, stderr) = await RunProcessAsync(
            "docker",
            BuildConfigArgs(repoRoot),
            repoRoot,
            env: new Dictionary<string, string?>
            {
                ["AI_FACTORY_API_IMAGE"] = "ghcr.io/example-owner/example-repo/api@sha256:1111111111111111111111111111111111111111111111111111111111111111",
                ["AI_FACTORY_WORKER_IMAGE"] = "ghcr.io/example-owner/example-repo/worker@sha256:2222222222222222222222222222222222222222222222222222222222222222",
            });

        Assert.True(exitCode == 0, $"`docker compose config -q` failed with exit code {exitCode}: {stderr}");
    }

    [Fact]
    public async Task DockerComposeConfig_WithoutWorkerImage_FailsLoudly()
    {
        if (!IsDockerAvailable())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var (exitCode, _, stderr) = await RunProcessAsync(
            "docker",
            BuildConfigArgs(repoRoot),
            repoRoot,
            env: new Dictionary<string, string?>
            {
                ["AI_FACTORY_API_IMAGE"] = "ghcr.io/example-owner/example-repo/api@sha256:1111111111111111111111111111111111111111111111111111111111111111",
                ["AI_FACTORY_WORKER_IMAGE"] = null,
            });

        Assert.NotEqual(0, exitCode);
        Assert.Contains("AI_FACTORY_WORKER_IMAGE must be set", stderr, StringComparison.Ordinal);
    }

    private static string BuildConfigArgs(string repoRoot)
    {
        var baseFile = Path.Combine(repoRoot, "docker-compose.yml");
        var releaseFile = Path.Combine(repoRoot, "docker-compose.release.yml");
        return $"compose -f \"{baseFile}\" -f \"{releaseFile}\" config -q";
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
            return false;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, Dictionary<string, string?>? env = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                if (value is null)
                {
                    startInfo.EnvironmentVariables.Remove(key);
                }
                else
                {
                    startInfo.EnvironmentVariables[key] = value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };
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
