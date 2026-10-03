using System.Diagnostics;
using System.Runtime.InteropServices;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Tests.Gateway;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.Cli;
using AI.Factory.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase N — shared process infrastructure, proven with REAL processes (no scripted runner). The real
/// <see cref="ClaudeCodeExecutionAdapter"/>/<see cref="CodexExecutionAdapter"/> and the real
/// <see cref="SystemProcessRunner"/> drive a fake CLI: a <c>.cmd</c> launching PowerShell, which reads stdin,
/// records its argv/environment, spawns a real grandchild (<c>ping</c>, inheriting the redirected pipes — the
/// worst case for a blocked reader) and then emits the events a CLI would. This is deterministic, offline and
/// free; it certifies the lifecycle mechanics (timeout, cancellation, process-tree kill, stdin closure, pipe
/// release, credential leakage, state cleanup) that every CLI runtime shares. It says nothing about any
/// model: no model is involved.
/// </summary>
public sealed class CliProcessTreeLifecycleTests : IDisposable
{
    private const string Key = "sk-ant-PHASEN-SYNTHETIC-MARKER-7f3a9c1e5b2d4a60";
    private const string AmbientKey = "sk-ant-PHASEN-AMBIENT-MARKER-0000deadbeef";
    private const string RoutedModel = "factory/model-a";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aif-phasen-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly string _workspace;
    private readonly string _stateRoot;
    private readonly string _probeDir;
    private readonly List<int> _spawned = new();

    public CliProcessTreeLifecycleTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        _probeDir = Directory.CreateDirectory(Path.Combine(_root, "probe")).FullName;
        _stateRoot = Path.Combine(_root, "state");
    }

    public void Dispose()
    {
        foreach (var pid in PidsRecorded())
        {
            Kill(pid);
        }

        TestTempDirectory.Delete(_root, _output);
    }

    public enum Runtime
    {
        ClaudeCode,
        Codex
    }

    private static bool Applicable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // ---------------------------------------------------------------- fake CLI

    private static string SuccessEvents(Runtime runtime, string text) => runtime == Runtime.ClaudeCode
        ? "'{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-haiku-4-5\",\"session_id\":\"s-1\"}'\r\n"
            + $"'{{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1,\"result\":\"{text}\",\"session_id\":\"s-1\",\"total_cost_usd\":0.01,\"usage\":{{\"input_tokens\":5,\"output_tokens\":2}}}}'"
        : "'{\"type\":\"thread.started\",\"thread_id\":\"t-1\"}'\r\n"
            + $"'{{\"type\":\"item.completed\",\"item\":{{\"id\":\"i1\",\"type\":\"agent_message\",\"text\":\"{text}\"}}}}'\r\n"
            + "'{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}'";

    private static string AuthFailureEvent(Runtime runtime) => runtime == Runtime.ClaudeCode
        ? "'{\"type\":\"system\",\"subtype\":\"api_retry\",\"attempt\":1,\"max_retries\":10,\"error_status\":401,\"error\":\"authentication_failed\"}'"
        : "'{\"type\":\"error\",\"message\":\"unexpected status 401 Unauthorized: Incorrect API key provided\"}'";

    /// <summary>Writes the fake CLI; <paramref name="body"/> is PowerShell run after stdin/argv/env were recorded.</summary>
    private string WriteFakeCli(string body, bool spawnGrandchild = true)
    {
        var dir = _probeDir;
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $d = '{{dir}}'
            [Console]::InputEncoding = [Text.Encoding]::UTF8
            $in = [Console]::In.ReadToEnd()
            [IO.File]::WriteAllText("$d\stdin.txt", $in)
            [IO.File]::WriteAllText("$d\env.txt", ((Get-ChildItem env: | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join "`n"))
            {{(spawnGrandchild
                ? "$c = Start-Process -FilePath ping -ArgumentList '-n','300','127.0.0.1' -NoNewWindow -PassThru\r\n"
                    + "[IO.File]::WriteAllText(\"$d\\pids.txt\", \"$PID`n$($c.Id)\")"
                : "[IO.File]::WriteAllText(\"$d\\pids.txt\", \"$PID\")")}}
            {{body}}
            """;
        var ps1 = Path.Combine(dir, "fake-cli.ps1");
        File.WriteAllText(ps1, script);

        var cmd = Path.Combine(dir, "fake-cli.cmd");
        File.WriteAllText(cmd,
            "@echo off\r\n"
            + $"echo %*> \"{ProbePath("args.txt")}\"\r\n" // argv is recorded by the batch itself: PowerShell never parses the CLI's flags
            + $"\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ps1}\"\r\n"
            + "exit /b %ERRORLEVEL%\r\n");
        return cmd;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
            }
        }
    }

    private CliExecutionAdapter CreateAdapter(Runtime runtime, string cmd, ILogger? logger = null)
    {
        var cli = new CliRuntimeOptions { StateRoot = _stateRoot };
        var runner = new SystemProcessRunner(new CommandPolicyOptions { AllowedCommands = new[] { "fake-cli" }, DeniedCommands = Array.Empty<string>() });
        var secrets = new FakeSecretProvider(new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = Key, ["OPENAI_API_KEY"] = Key });

        return runtime == Runtime.ClaudeCode
            ? new ClaudeCodeExecutionAdapter(
                new ClaudeCodeOptions { ExecutablePath = cmd, Models = new() { [RoutedModel] = "claude-haiku-4-5" } },
                cli, runner, secrets, (ILogger<ClaudeCodeExecutionAdapter>?)logger ?? new ListLogger<ClaudeCodeExecutionAdapter>())
            : new CodexExecutionAdapter(
                new CodexOptions { ExecutablePath = cmd, Models = new() { [RoutedModel] = "gpt-5-codex" } },
                cli, runner, secrets, (ILogger<CodexExecutionAdapter>?)logger ?? new ListLogger<CodexExecutionAdapter>());
    }

    private AgentExecutionSpec Spec(DateTimeOffset? deadline = null, string instructions = "Do the work.") => new(
        AgentRunId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        ProjectId: Guid.NewGuid(),
        WorkItemId: Guid.NewGuid(),
        Attempt: 1,
        Iteration: 0,
        Agent: new ExecutionAgent(Guid.NewGuid(), "coder", "Implementation", "SYSTEM PROMPT"),
        Instructions: instructions,
        Workspace: new ExecutionWorkspace(_workspace),
        Model: new ModelRoute(RoutedModel, "openrouter"),
        Timeout: TimeSpan.FromMinutes(5),
        Deadline: deadline);

    // ---------------------------------------------------------------- process helpers

    private string ProbePath(string name) => Path.Combine(_probeDir, name);

    private int[] PidsRecorded() =>
        File.Exists(ProbePath("pids.txt"))
            ? File.ReadAllLines(ProbePath("pids.txt")).Select(l => int.TryParse(l.Trim(), out var p) ? p : 0).Where(p => p > 0).ToArray()
            : Array.Empty<int>();

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan limit)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < limit)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private async Task AssertTreeGone()
    {
        var pids = PidsRecorded();
        Assert.True(pids.Length >= 2, "the fake CLI should have recorded its own pid and its grandchild's");
        foreach (var pid in pids)
        {
            Assert.True(await WaitUntil(() => !IsAlive(pid), TimeSpan.FromSeconds(5)),
                $"process {pid} of the CLI's tree is still alive after the run ended");
        }
    }

    private static void AssertStatus(ExecutionOutcomeStatus expected, ExecutionOutcome outcome) =>
        Assert.True(expected == outcome.Status,
            $"expected {expected} but was {outcome.Status} (exit '{outcome.ExitReason}', category {outcome.FailureCategory}): {outcome.ErrorMessage}");

    private void AssertNoRuntimeStateLeft() =>
        Assert.True(!Directory.Exists(_stateRoot) || Directory.GetFileSystemEntries(_stateRoot).Length == 0,
            "the per-run CLI state directory must be removed when the run ends");

    // ---------------------------------------------------------------- timeout

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task Timeout_KillsTheWholeProcessTree_ReportsTimedOut_AndRemovesState(Runtime runtime)
    {
        if (!Applicable) { return; }

        var cmd = WriteFakeCli("Start-Sleep -Seconds 300");
        var adapter = CreateAdapter(runtime, cmd);
        var stopwatch = Stopwatch.StartNew();

        var outcome = await adapter.ExecuteAsync(Spec(deadline: DateTimeOffset.UtcNow.AddSeconds(6)));

        stopwatch.Stop();
        AssertStatus(ExecutionOutcomeStatus.TimedOut, outcome);
        Assert.Equal(FailureCategory.Timeout, outcome.FailureCategory);
        Assert.Equal("timeout", outcome.ExitReason);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(25), $"deadline 6s, took {stopwatch.Elapsed}");
        await AssertTreeGone();
        AssertNoRuntimeStateLeft();
    }

    // ---------------------------------------------------------------- cancellation

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task Cancellation_DuringActiveExecution_KillsTheTree_AndIsCancelledNotFailedOrTimedOut(Runtime runtime)
    {
        if (!Applicable) { return; }

        var cmd = WriteFakeCli("Start-Sleep -Seconds 300");
        var adapter = CreateAdapter(runtime, cmd);
        using var cts = new CancellationTokenSource();

        var run = adapter.ExecuteAsync(Spec(), cts.Token);
        Assert.True(await WaitUntil(() => PidsRecorded().Length >= 2, TimeSpan.FromSeconds(30)), "fake CLI never became active");
        cts.Cancel();
        var outcome = await run;

        AssertStatus(ExecutionOutcomeStatus.Cancelled, outcome);
        Assert.Equal(FailureCategory.Cancelled, outcome.FailureCategory);
        await AssertTreeGone();
        AssertNoRuntimeStateLeft();
    }

    [Fact]
    public async Task Cancellation_ImmediatelyAfterLaunch_LeavesNoProcessBehind()
    {
        if (!Applicable) { return; }

        var cmd = WriteFakeCli("Start-Sleep -Seconds 300");
        var adapter = CreateAdapter(Runtime.ClaudeCode, cmd);
        using var cts = new CancellationTokenSource();

        // Cancel the moment the process record exists, i.e. the child has just been launched — likely before
        // the PowerShell script has even spawned its grandchild.
        var run = adapter.ExecuteAsync(Spec(), cts.Token);
        Assert.True(await WaitUntil(
            () => Directory.Exists(_stateRoot) && Directory.EnumerateFiles(_stateRoot, "process.json", SearchOption.AllDirectories).Any(),
            TimeSpan.FromSeconds(30)));
        cts.Cancel();
        var outcome = await run;

        AssertStatus(ExecutionOutcomeStatus.Cancelled, outcome);
        foreach (var pid in PidsRecorded())
        {
            Assert.True(await WaitUntil(() => !IsAlive(pid), TimeSpan.FromSeconds(5)), $"process {pid} survived the cancellation");
        }

        // The script may have been killed between "spawn grandchild" and "record pids": give an orphan nothing to hide behind.
        await Task.Delay(1500);
        AssertNoRuntimeStateLeft();
        Assert.Empty(Directory.GetFileSystemEntries(_workspace));
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task Cancellation_BeforeLaunch_NeverReportsFailureOrSuccess(Runtime runtime)
    {
        if (!Applicable) { return; }

        var cmd = WriteFakeCli("Start-Sleep -Seconds 300");
        var adapter = CreateAdapter(runtime, cmd);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await adapter.ExecuteAsync(Spec(), cts.Token);

        AssertStatus(ExecutionOutcomeStatus.Cancelled, outcome);
        foreach (var pid in PidsRecorded())
        {
            Assert.True(await WaitUntil(() => !IsAlive(pid), TimeSpan.FromSeconds(5)), $"process {pid} survived the cancellation");
        }

        await Task.Delay(1500);
        AssertNoRuntimeStateLeft();
    }

    // ---------------------------------------------------------------- deterministic auth failure (no multi-minute retry loop)

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task AuthenticationFailure_StopsTheRunOnTheFirstEvent_AndKillsTheTree(Runtime runtime)
    {
        if (!Applicable) { return; }

        // The CLI would sit in its own retry loop for minutes (Start-Sleep 300 stands in for it).
        var cmd = WriteFakeCli($"Write-Output {AuthFailureEvent(runtime)}\r\nStart-Sleep -Seconds 300");
        var adapter = CreateAdapter(runtime, cmd);
        var stopwatch = Stopwatch.StartNew();

        var outcome = await adapter.ExecuteAsync(Spec());

        stopwatch.Stop();
        AssertStatus(ExecutionOutcomeStatus.Failed, outcome);
        Assert.Equal(FailureCategory.Provider, outcome.FailureCategory);
        Assert.Equal("Authentication", outcome.GatewayFailure);
        Assert.Equal("aborted", outcome.ExitReason);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"took {stopwatch.Elapsed}");
        await AssertTreeGone();
        AssertNoRuntimeStateLeft();
    }

    // ---------------------------------------------------------------- stdin closure + pipe release

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task LargePrompt_IsDeliveredCompletely_AndStdinIsClosed(Runtime runtime)
    {
        if (!Applicable) { return; }

        // The fake reads stdin to EOF before it emits anything: it can only finish if the runner closed stdin.
        var prompt = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"line {i} ünïcödé ✓"));
        var cmd = WriteFakeCli(SuccessEvents(runtime, "DONE") + "\r\nexit 0", spawnGrandchild: false);

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec(deadline: DateTimeOffset.UtcNow.AddMinutes(1), instructions: prompt));

        AssertStatus(ExecutionOutcomeStatus.Succeeded, outcome);
        var received = File.ReadAllText(ProbePath("stdin.txt"));
        Assert.Equal("SYSTEM PROMPT\n\n" + prompt, received.Replace("\r\n", "\n"));
        AssertNoRuntimeStateLeft();
    }

    /// <summary>
    /// The CLI finished and exited 0, but a process it started (a build server, a node-reuse worker) keeps the
    /// inherited stdout/stderr pipes open. The run must complete promptly instead of waiting for that process.
    /// </summary>
    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task FinishedCli_WithALingeringGrandchildHoldingThePipes_DoesNotBlockTheRun(Runtime runtime)
    {
        if (!Applicable) { return; }

        var cmd = WriteFakeCli(SuccessEvents(runtime, "DONE") + "\r\nexit 0");
        var stopwatch = Stopwatch.StartNew();

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec(deadline: DateTimeOffset.UtcNow.AddSeconds(45)));

        stopwatch.Stop();
        AssertStatus(ExecutionOutcomeStatus.Succeeded, outcome);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"blocked on the grandchild's pipes for {stopwatch.Elapsed}");
        AssertNoRuntimeStateLeft();
    }

    // ---------------------------------------------------------------- success semantics through a real process

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task ExitZeroWithoutAResultEvent_IsNeverSuccess_WithARealProcess(Runtime runtime)
    {
        if (!Applicable) { return; }

        var cmd = WriteFakeCli("Write-Output 'not json at all'\r\nWrite-Output '{\"type\":\"assistant\"}'\r\nexit 0", spawnGrandchild: false);

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec());

        AssertStatus(ExecutionOutcomeStatus.Failed, outcome);
        Assert.Contains("success cannot be proven", outcome.ErrorMessage);
        AssertNoRuntimeStateLeft();
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task ExitZeroWithPartialFinalEvent_IsNeverSuccess(Runtime runtime)
    {
        if (!Applicable) { return; }

        // The final event is cut off mid-JSON (a killed or crashed CLI): it must not count as a completion.
        var truncated = runtime == Runtime.ClaudeCode
            ? "'{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":fal'"
            : "'{\"type\":\"turn.completed\",\"usage\":{\"input_tok'";
        var cmd = WriteFakeCli($"Write-Output {truncated}\r\nexit 0", spawnGrandchild: false);

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec());

        AssertStatus(ExecutionOutcomeStatus.Failed, outcome);
        AssertNoRuntimeStateLeft();
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task SuccessfulStructuredResult_WithARealProcess_Succeeds_WithUsageAndSession(Runtime runtime)
    {
        if (!Applicable) { return; }

        var cmd = WriteFakeCli(SuccessEvents(runtime, "DONE") + "\r\nexit 0", spawnGrandchild: false);

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec());

        AssertStatus(ExecutionOutcomeStatus.Succeeded, outcome);
        Assert.Equal("DONE", outcome.Output);
        Assert.Equal(7, outcome.Usage?.TotalTokens);
        Assert.Equal(runtime == Runtime.ClaudeCode ? "s-1" : "t-1", outcome.Report?.SessionId);
        Assert.Equal(runtime == Runtime.ClaudeCode ? (decimal?)0.01m : null, outcome.Report?.CostUsd);
    }

    // ---------------------------------------------------------------- credential leakage matrix

    [Theory]
    [InlineData(Runtime.ClaudeCode, "ANTHROPIC_API_KEY")]
    [InlineData(Runtime.Codex, "CODEX_API_KEY")]
    public async Task SyntheticSecret_ReachesOnlyTheChildEnvironment_AndNoOtherSurface(Runtime runtime, string credentialVariable)
    {
        if (!Applicable) { return; }

        // The fake CLI misbehaves on purpose: it prints the credential into its result text and into stderr,
        // as a model-driven CLI could. Both must be redacted before they reach the outcome or any log.
        var echo = $"[Console]::Error.WriteLine(\"leaked \" + $env:{credentialVariable})\r\n"
            + SuccessEvents(runtime, "key is ' + $env:" + credentialVariable + " + '") + "\r\nexit 0";
        var cmd = WriteFakeCli(echo, spawnGrandchild: false);
        var logger = runtime == Runtime.ClaudeCode ? (ILogger)new ListLogger<ClaudeCodeExecutionAdapter>() : new ListLogger<CodexExecutionAdapter>();
        var adapter = CreateAdapter(runtime, cmd, logger);
        var before = Directory.GetFileSystemEntries(_workspace, "*", SearchOption.AllDirectories);

        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", AmbientKey);
        Environment.SetEnvironmentVariable("CODEX_API_KEY", AmbientKey);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", AmbientKey);
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", AmbientKey);
        try
        {
            var outcome = await adapter.ExecuteAsync(Spec());

            // argv
            var args = File.ReadAllText(ProbePath("args.txt"));
            Assert.DoesNotContain(Key, args);
            Assert.DoesNotContain(AmbientKey, args);

            // stdin (the prompt)
            var stdin = File.ReadAllText(ProbePath("stdin.txt"));
            Assert.DoesNotContain(Key, stdin);
            Assert.DoesNotContain(AmbientKey, stdin);

            // environment: the Factory credential under exactly one variable, no ambient credential anywhere
            var env = File.ReadAllLines(ProbePath("env.txt"));
            Assert.Equal(1, env.Count(l => l.Contains(Key, StringComparison.Ordinal)));
            Assert.Contains($"{credentialVariable}={Key}", env);
            Assert.DoesNotContain(env, l => l.Contains(AmbientKey, StringComparison.Ordinal));
            Assert.DoesNotContain(env, l => l.StartsWith("CLAUDE_CODE_OAUTH_TOKEN=", StringComparison.OrdinalIgnoreCase));
            // Other ambient session/provider-routing variables of the host (a developer machine running Claude Code
            // has many CLAUDE_CODE_* and may set ANTHROPIC_BASE_URL) must not reach the child either.
            Assert.DoesNotContain(env, l => l.StartsWith("ANTHROPIC_BASE_URL=", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(env, l => l.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                && !l.StartsWith("CLAUDE_CODE_GIT_BASH_PATH=", StringComparison.OrdinalIgnoreCase)
                && !l.StartsWith("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=", StringComparison.OrdinalIgnoreCase)); // the adapter's own setting
            if (runtime == Runtime.ClaudeCode)
            {
                Assert.DoesNotContain(env, l => l.StartsWith("OPENAI_API_KEY=", StringComparison.OrdinalIgnoreCase));
            }

            // outcome (output, error, report) and logs
            AssertStatus(ExecutionOutcomeStatus.Succeeded, outcome);
            Assert.DoesNotContain(Key, outcome.Output ?? string.Empty);
            Assert.DoesNotContain(Key, outcome.ErrorMessage ?? string.Empty);
            Assert.Contains("[REDACTED]", outcome.Output);
            var logs = string.Join('\n', runtime == Runtime.ClaudeCode
                ? ((ListLogger<ClaudeCodeExecutionAdapter>)logger).Lines
                : ((ListLogger<CodexExecutionAdapter>)logger).Lines);
            Assert.DoesNotContain(Key, logs);
            Assert.DoesNotContain(AmbientKey, logs);
            Assert.Contains("leaked", logs); // stderr was logged — redacted

            // workspace, runtime home, temp files
            Assert.Equal(before, Directory.GetFileSystemEntries(_workspace, "*", SearchOption.AllDirectories));
            AssertNoRuntimeStateLeft();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("CODEX_API_KEY", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
        }
    }
}
