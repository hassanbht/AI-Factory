using AI.Factory.Infrastructure.Agents;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class SystemProcessRunnerTests
{
    // These tests exercise SystemProcessRunner's own mechanics (timeout,
    // cancellation, stdout capture, exit code, launch failure) using
    // cmd.exe as a convenient trivial-command runner — orthogonal to the
    // command allow/deny policy, so this instance is deliberately
    // permissive rather than using the production default.
    private readonly SystemProcessRunner _runner = new(new CommandPolicyOptions
    {
        AllowedCommands = new[] { "cmd", "definitely-not-a-real-executable-abc123xyz" },
        DeniedCommands = Array.Empty<string>()
    });

    [Fact]
    public async Task RunAsync_CapturesStdoutAndExitCode_ForSuccessfulProcess()
    {
        var result = await _runner.RunAsync(
            new ProcessRunRequest("cmd.exe", new[] { "/c", "echo", "hello-from-test" }, TimeSpan.FromSeconds(10)));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("hello-from-test", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZeroExitCode_ForFailingProcess()
    {
        var result = await _runner.RunAsync(
            new ProcessRunRequest("cmd.exe", new[] { "/c", "exit", "3" }, TimeSpan.FromSeconds(10)));

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
        // `timeout`/`Start-Sleep` need an interactive console and exit
        // immediately without one; `ping` needs no console and reliably
        // takes ~10s for 11 pings, making it a safe long-running command.
        var result = await _runner.RunAsync(
            new ProcessRunRequest("cmd.exe", new[] { "/c", "ping", "-n", "11", "127.0.0.1" }, TimeSpan.FromSeconds(1)));

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellation_WithoutTreatingItAsTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runner.RunAsync(
                new ProcessRunRequest("cmd.exe", new[] { "/c", "ping", "-n", "11", "127.0.0.1" }, TimeSpan.FromSeconds(30)),
                cts.Token));
    }
}
