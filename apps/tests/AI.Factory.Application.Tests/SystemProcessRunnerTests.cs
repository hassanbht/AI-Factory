using System.Runtime.InteropServices;
using AI.Factory.Infrastructure.Agents;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class SystemProcessRunnerTests
{
    // These tests exercise SystemProcessRunner's own mechanics (timeout,
    // cancellation, stdout capture, exit code, launch failure) using
    // a standard platform shell (cmd.exe on Windows, sh on Linux) as a
    // convenient trivial-command runner — orthogonal to the command allow/deny
    // policy, so this instance is deliberately permissive.
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly string ShellExe = IsWindows ? "cmd.exe" : "sh";

    private readonly SystemProcessRunner _runner = new(new CommandPolicyOptions
    {
        AllowedCommands = new[] { "cmd", "cmd.exe", "sh", "definitely-not-a-real-executable-abc123xyz" },
        DeniedCommands = Array.Empty<string>()
    });

    [Fact]
    public async Task RunAsync_CapturesStdoutAndExitCode_ForSuccessfulProcess()
    {
        var args = IsWindows
            ? new[] { "/c", "echo", "hello-from-test" }
            : new[] { "-c", "echo hello-from-test" };

        var result = await _runner.RunAsync(
            new ProcessRunRequest(ShellExe, args, TimeSpan.FromSeconds(10)));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("hello-from-test", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZeroExitCode_ForFailingProcess()
    {
        var args = IsWindows
            ? new[] { "/c", "exit", "3" }
            : new[] { "-c", "exit 3" };

        var result = await _runner.RunAsync(
            new ProcessRunRequest(ShellExe, args, TimeSpan.FromSeconds(10)));

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_ThrowsProcessLaunchException_ForMissingExecutable()
    {
        await Assert.ThrowsAsync<ProcessLaunchException>(() =>
            _runner.RunAsync(
                new ProcessRunRequest(
                    "definitely-not-a-real-executable-abc123xyz",
                    Array.Empty<string>(),
                    TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task RunAsync_TimesOutAndKillsProcess_ForLongRunningCommand()
    {
        var args = IsWindows
            ? new[] { "/c", "ping", "-n", "11", "127.0.0.1" }
            : new[] { "-c", "sleep 10" };

        var result = await _runner.RunAsync(
            new ProcessRunRequest(ShellExe, args, TimeSpan.FromSeconds(1)));

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellation_WithoutTreatingItAsTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var args = IsWindows
            ? new[] { "/c", "ping", "-n", "11", "127.0.0.1" }
            : new[] { "-c", "sleep 30" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runner.RunAsync(
                new ProcessRunRequest(ShellExe, args, TimeSpan.FromSeconds(30)),
                cts.Token));
    }
}
