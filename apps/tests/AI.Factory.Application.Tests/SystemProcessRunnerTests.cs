using System.Runtime.InteropServices;
using AI.Factory.Infrastructure.Agents;
using Xunit;
using AI.Factory.TestSupport;

namespace AI.Factory.Application.Tests;

[Collection(TestCollections.RealProcesses)]
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

    /// <summary>
    /// Phase O (O7) — a failure mode none of the others cover: a child that writes megabytes to BOTH pipes. With synchronous
    /// reading, or one pipe read after the other, the child blocks on a full pipe and the run deadlocks until the timeout.
    /// Everything must be captured, exit code intact, well inside the deadline.
    /// </summary>
    [Fact]
    public async Task RunAsync_MegabytesOnBothStdoutAndStderr_DoesNotDeadlock_AndIsCapturedInFull()
    {
        const int lines = 20_000;
        var args = IsWindows
            ? new[] { "/c", "for /L %i in (1,1,20000) do @(echo out%i xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx& echo err%i xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx 1>&2)" }
            : new[] { "-c", "i=1; while [ $i -le 20000 ]; do echo out$i xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx; echo err$i xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx 1>&2; i=$((i+1)); done" };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await _runner.RunAsync(new ProcessRunRequest(ShellExe, args, TimeSpan.FromSeconds(90)));

        stopwatch.Stop();
        Assert.False(result.TimedOut, $"deadlocked on a full pipe: took {stopwatch.Elapsed}");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(lines, result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(lines, result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains($"out{lines} ", result.StandardOutput);
        Assert.Contains($"err{lines} ", result.StandardError);
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

    /// <summary>
    /// Phase M review — a child that never reads stdin used to block the (multi-KB) prompt write forever,
    /// before the timeout even existed. The write now runs alongside the wait; the timeout kills the child.
    /// </summary>
    [Fact]
    public async Task RunAsync_ChildThatNeverReadsALargeStdin_StillTimesOut()
    {
        var args = IsWindows
            ? new[] { "/c", "ping", "-n", "30", "127.0.0.1" }
            : new[] { "-c", "sleep 30" };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await _runner.RunAsync(new ProcessRunRequest(
            ShellExe, args, TimeSpan.FromSeconds(2), StandardInput: new string('x', 4 * 1024 * 1024)));

        Assert.True(result.TimedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"took {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Phase N — the root exits 0 while a process it started keeps the inherited stdout pipe open. The run must
    /// end with the root's own exit code, not wait for the descendant until the timeout (which reported a
    /// finished CLI as TimedOut).
    /// </summary>
    [WindowsFact]
    public async Task RunAsync_RootExitedButADescendantHoldsThePipes_ReturnsTheRootExitCode_NotATimeout()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await _runner.RunAsync(new ProcessRunRequest(
            ShellExe, new[] { "/c", "start", "/b", "ping", "-n", "25", "127.0.0.1" }, TimeSpan.FromSeconds(60)));

        Assert.False(result.TimedOut, "the root process had already finished");
        Assert.Equal(0, result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"waited {stopwatch.Elapsed} for the descendant's pipes");
    }

    /// <summary>Phase M review — an observer exception on the output thread must not crash the process.</summary>
    [Fact]
    public async Task RunAsync_ThrowingOutputObserver_DoesNotBreakTheRun()
    {
        var args = IsWindows ? new[] { "/c", "echo", "line" } : new[] { "-c", "echo line" };

        var result = await _runner.RunAsync(new ProcessRunRequest(
            ShellExe, args, TimeSpan.FromSeconds(10), OnStandardOutputLine: _ => throw new InvalidOperationException("observer bug")));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("line", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_ThrowingOnStartedCallback_KillsTheChild_AndRethrows()
    {
        var args = IsWindows ? new[] { "/c", "ping", "-n", "30", "127.0.0.1" } : new[] { "-c", "sleep 30" };
        var pid = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.RunAsync(new ProcessRunRequest(
            ShellExe, args, TimeSpan.FromSeconds(30),
            OnStarted: p => { pid = p.Id; throw new InvalidOperationException("record failed"); })));

        Assert.NotEqual(0, pid);
        bool alive;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            alive = !process.HasExited;
        }
        catch (ArgumentException)
        {
            alive = false;
        }

        Assert.False(alive, "a child must never outlive a failed RunAsync");
    }
}
