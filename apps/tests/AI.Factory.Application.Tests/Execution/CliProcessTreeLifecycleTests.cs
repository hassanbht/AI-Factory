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
using AI.Factory.TestSupport;

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
[Collection(TestCollections.RealProcesses)]
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

    /// <summary>Phase O — a failed assertion names the surface, never the value (xunit's DoesNotContain would print the whole haystack).</summary>
    private static void AssertNoSecret(string? haystack, string surface, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.False(
                (haystack ?? string.Empty).Contains(secret, StringComparison.Ordinal),
                $"a credential reached: {surface}");
        }
    }

    private static void AssertStatus(ExecutionOutcomeStatus expected, ExecutionOutcome outcome) =>
        Assert.True(expected == outcome.Status,
            $"expected {expected} but was {outcome.Status} (exit '{outcome.ExitReason}', category {outcome.FailureCategory}): {outcome.ErrorMessage}");

    private void AssertNoRuntimeStateLeft() =>
        Assert.True(!Directory.Exists(_stateRoot) || Directory.GetFileSystemEntries(_stateRoot).Length == 0,
            "the per-run CLI state directory must be removed when the run ends");

    // ---------------------------------------------------------------- timeout

    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task Timeout_KillsTheWholeProcessTree_ReportsTimedOut_AndRemovesState(Runtime runtime)
    {
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

    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task Cancellation_DuringActiveExecution_KillsTheTree_AndIsCancelledNotFailedOrTimedOut(Runtime runtime)
    {
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

    [WindowsFact]
    public async Task Cancellation_ImmediatelyAfterLaunch_LeavesNoProcessBehind()
    {
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

    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task Cancellation_BeforeLaunch_NeverReportsFailureOrSuccess(Runtime runtime)
    {
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

    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task AuthenticationFailure_StopsTheRunOnTheFirstEvent_AndKillsTheTree(Runtime runtime)
    {
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

    /// <summary>
    /// Phase O (O11) — Claude Code retries a 401 itself (up to 10 times, minutes). The Factory must stay the authority:
    /// the CLI is stopped at the first 401 and what it would have printed later (here a "success" result after its retries)
    /// can never turn the run into a success.
    /// </summary>
    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task AuthenticationFailure_NeverBecomesSuccess_EvenIfTheCliWouldLaterReportOne(Runtime runtime)
    {
        var lateSuccess = SuccessEvents(runtime, "LATE-SUCCESS");
        var cmd = WriteFakeCli(
            $"Write-Output {AuthFailureEvent(runtime)}\r\nStart-Sleep -Seconds 8\r\n{lateSuccess}\r\nexit 0");

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec(deadline: DateTimeOffset.UtcNow.AddMinutes(2)));

        AssertStatus(ExecutionOutcomeStatus.Failed, outcome);
        Assert.Equal("Authentication", outcome.GatewayFailure);
        Assert.DoesNotContain("LATE-SUCCESS", outcome.Output ?? string.Empty);
        await AssertTreeGone(); // killed at the first 401, long before its 8 s "retries" ended
        AssertNoRuntimeStateLeft();
    }

    /// <summary>
    /// Phase O (O11) — a CLI that keeps emitting retry/progress events forever is not "making progress" in the Factory's
    /// eyes: the configured deadline stays authoritative and ends it as TimedOut (a failure mode the silent-hang test above
    /// does not cover — here the pipes are busy the whole time).
    /// </summary>
    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task CliThatKeepsEmittingRetryEventsForever_IsStoppedByTheFactoryDeadline(Runtime runtime)
    {
        var transient = runtime == Runtime.ClaudeCode
            ? "'{\"type\":\"system\",\"subtype\":\"api_retry\",\"attempt\":1,\"error_status\":529,\"error\":\"overloaded\"}'"
            : "'{\"type\":\"error\",\"message\":\"stream disconnected, retrying\"}'";
        var cmd = WriteFakeCli($"while ($true) {{ Write-Output {transient}; Start-Sleep -Milliseconds 200 }}");
        var stopwatch = Stopwatch.StartNew();

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec(deadline: DateTimeOffset.UtcNow.AddSeconds(6)));

        stopwatch.Stop();
        AssertStatus(ExecutionOutcomeStatus.TimedOut, outcome);
        Assert.Equal(FailureCategory.Timeout, outcome.FailureCategory);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(25), $"deadline 6s, took {stopwatch.Elapsed}");
        await AssertTreeGone();
        AssertNoRuntimeStateLeft();
    }

    // ---------------------------------------------------------------- non-zero exit and the stderr pipe

    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task NonZeroExit_WithStderr_IsFailed_NeverSuccess_AndStderrIsLoggedRedacted(Runtime runtime)
    {
        // A result-looking event on stdout does not rescue a CLI that exited non-zero. stderr is a separate pipe: it is read
        // in full, redacted, and logged (not echoed into the persisted error); the exit code is the recorded reason.
        var cmd = WriteFakeCli(
            "[Console]::Error.WriteLine(\"fatal: cannot continue; token=\" + $env:ANTHROPIC_API_KEY + $env:CODEX_API_KEY)\r\n"
            + SuccessEvents(runtime, "DONE") + "\r\nexit 2",
            spawnGrandchild: false);
        var logger = runtime == Runtime.ClaudeCode ? (ILogger)new ListLogger<ClaudeCodeExecutionAdapter>() : new ListLogger<CodexExecutionAdapter>();

        var outcome = await CreateAdapter(runtime, cmd, logger).ExecuteAsync(Spec());

        AssertStatus(ExecutionOutcomeStatus.Failed, outcome);
        Assert.Equal("exit:2", outcome.ExitReason);
        Assert.Contains("exited with code 2", outcome.ErrorMessage);
        AssertNoSecret(outcome.ErrorMessage, "the failure message", Key, AmbientKey);

        var logs = string.Join('\n', runtime == Runtime.ClaudeCode
            ? ((ListLogger<ClaudeCodeExecutionAdapter>)logger).Lines
            : ((ListLogger<CodexExecutionAdapter>)logger).Lines);
        Assert.Contains("fatal: cannot continue", logs); // the stderr pipe was drained and surfaced
        AssertNoSecret(logs, "the logs", Key, AmbientKey);
        AssertNoRuntimeStateLeft();
    }

    // ---------------------------------------------------------------- stdin closure + pipe release

    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task LargePrompt_IsDeliveredCompletely_AndStdinIsClosed(Runtime runtime)
    {
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
    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task FinishedCli_WithALingeringGrandchildHoldingThePipes_DoesNotBlockTheRun(Runtime runtime)
    {
        var cmd = WriteFakeCli(SuccessEvents(runtime, "DONE") + "\r\nexit 0");
        var stopwatch = Stopwatch.StartNew();

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec(deadline: DateTimeOffset.UtcNow.AddSeconds(45)));

        stopwatch.Stop();
        AssertStatus(ExecutionOutcomeStatus.Succeeded, outcome);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"blocked on the grandchild's pipes for {stopwatch.Elapsed}");
        AssertNoRuntimeStateLeft();
    }

    // ---------------------------------------------------------------- success semantics through a real process

    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task ExitZeroWithoutAResultEvent_IsNeverSuccess_WithARealProcess(Runtime runtime)
    {
        var cmd = WriteFakeCli("Write-Output 'not json at all'\r\nWrite-Output '{\"type\":\"assistant\"}'\r\nexit 0", spawnGrandchild: false);

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec());

        AssertStatus(ExecutionOutcomeStatus.Failed, outcome);
        Assert.Contains("success cannot be proven", outcome.ErrorMessage);
        AssertNoRuntimeStateLeft();
    }

    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task ExitZeroWithPartialFinalEvent_IsNeverSuccess(Runtime runtime)
    {
        // The final event is cut off mid-JSON (a killed or crashed CLI): it must not count as a completion.
        var truncated = runtime == Runtime.ClaudeCode
            ? "'{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":fal'"
            : "'{\"type\":\"turn.completed\",\"usage\":{\"input_tok'";
        var cmd = WriteFakeCli($"Write-Output {truncated}\r\nexit 0", spawnGrandchild: false);

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec());

        AssertStatus(ExecutionOutcomeStatus.Failed, outcome);
        AssertNoRuntimeStateLeft();
    }

    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task SuccessfulStructuredResult_WithARealProcess_Succeeds_WithUsageAndSession(Runtime runtime)
    {
        var cmd = WriteFakeCli(SuccessEvents(runtime, "DONE") + "\r\nexit 0", spawnGrandchild: false);

        var outcome = await CreateAdapter(runtime, cmd).ExecuteAsync(Spec());

        AssertStatus(ExecutionOutcomeStatus.Succeeded, outcome);
        Assert.Equal("DONE", outcome.Output);
        Assert.Equal(7, outcome.Usage?.TotalTokens);
        Assert.Equal(runtime == Runtime.ClaudeCode ? "s-1" : "t-1", outcome.Report?.SessionId);
        Assert.Equal(runtime == Runtime.ClaudeCode ? (decimal?)0.01m : null, outcome.Report?.CostUsd);
    }

    // ---------------------------------------------------------------- credential leakage matrix

    [WindowsTheory]
    [InlineData(Runtime.ClaudeCode, "ANTHROPIC_API_KEY")]
    [InlineData(Runtime.Codex, "CODEX_API_KEY")]
    public async Task SyntheticSecret_ReachesOnlyTheChildEnvironment_AndNoOtherSurface(Runtime runtime, string credentialVariable)
    {
        // The fake CLI misbehaves on purpose: it prints the credential into its result text and into stderr,
        // as a model-driven CLI could. Both must be redacted before they reach the outcome or any log.
        var echo = $"[Console]::Error.WriteLine(\"leaked \" + $env:{credentialVariable})\r\n"
            + SuccessEvents(runtime, "key is ' + $env:" + credentialVariable + " + '") + "\r\nexit 0";
        var cmd = WriteFakeCli(echo, spawnGrandchild: false);
        var logger = runtime == Runtime.ClaudeCode ? (ILogger)new ListLogger<ClaudeCodeExecutionAdapter>() : new ListLogger<CodexExecutionAdapter>();
        var adapter = CreateAdapter(runtime, cmd, logger);
        var before = Directory.GetFileSystemEntries(_workspace, "*", SearchOption.AllDirectories);

        // Phase O (O10) — every ambient credential, provider-routing and CLI-home variable a developer machine can carry. None may
        // reach the child; the CLI's home must be this run's own directory, never the user's.
        var ambientHome = Directory.CreateDirectory(Path.Combine(_root, "ambient-user-home")).FullName;
        var ambient = new Dictionary<string, string?>
        {
            ["ANTHROPIC_API_KEY"] = AmbientKey,
            ["CODEX_API_KEY"] = AmbientKey,
            ["OPENAI_API_KEY"] = AmbientKey,
            ["CLAUDE_CODE_OAUTH_TOKEN"] = AmbientKey,
            ["ANTHROPIC_BASE_URL"] = "http://ambient-routing.invalid:1/anthropic",
            ["ANTHROPIC_AUTH_TOKEN"] = AmbientKey,
            ["OPENAI_BASE_URL"] = "http://ambient-routing.invalid:2/openai",
            ["OPENAI_API_BASE"] = "http://ambient-routing.invalid:3/openai",
            ["OPENAI_ORG_ID"] = "org-ambient-marker",
            ["CLAUDE_CONFIG_DIR"] = ambientHome,
            ["CODEX_HOME"] = ambientHome
        };
        var previous = ambient.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (name, value) in ambient)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            var outcome = await adapter.ExecuteAsync(Spec());

            // argv
            var args = File.ReadAllText(ProbePath("args.txt"));
            AssertNoSecret(args, "argv", Key, AmbientKey);
            Assert.False(args.Contains("ambient-routing.invalid", StringComparison.Ordinal), "an ambient routing URL reached argv");

            // stdin (the prompt)
            var stdin = File.ReadAllText(ProbePath("stdin.txt"));
            AssertNoSecret(stdin, "stdin (the prompt)", Key, AmbientKey);

            // environment: the Factory credential under exactly one variable, no ambient credential anywhere
            var env = File.ReadAllLines(ProbePath("env.txt"));
            Assert.Equal(1, env.Count(l => l.Contains(Key, StringComparison.Ordinal)));
            Assert.True(env.Contains($"{credentialVariable}={Key}"), $"the Factory credential is not in {credentialVariable}");
            Assert.False(env.Any(l => l.Contains(AmbientKey, StringComparison.Ordinal)), "an ambient credential reached the child environment");
            Assert.False(env.Any(l => l.Contains("ambient-routing.invalid", StringComparison.Ordinal)), "an ambient provider-routing URL reached the child environment");
            Assert.False(env.Any(l => l.Contains("org-ambient-marker", StringComparison.Ordinal)), "an ambient organization id reached the child environment");
            Assert.DoesNotContain(env, l => l.StartsWith("CLAUDE_CODE_OAUTH_TOKEN=", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(env, l => l.StartsWith("ANTHROPIC_AUTH_TOKEN=", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(env, l => l.StartsWith("OPENAI_BASE_URL=", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("OPENAI_API_BASE=", StringComparison.OrdinalIgnoreCase));

            // The CLI's home, config and temp locations are this run's own directory under the Factory state root:
            // never the user's profile (~/.claude, ~/.codex) and never the ambient CLAUDE_CONFIG_DIR / CODEX_HOME.
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var variable in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "CLAUDE_CONFIG_DIR", "CODEX_HOME" })
            {
                var line = env.FirstOrDefault(l => l.StartsWith(variable + "=", StringComparison.OrdinalIgnoreCase));
                if (line is null)
                {
                    continue; // not every CLI needs every variable
                }

                var value = line[(variable.Length + 1)..];
                Assert.True(
                    value.StartsWith(_stateRoot, StringComparison.OrdinalIgnoreCase),
                    $"{variable} is not inside the per-run state root");
                Assert.False(value.StartsWith(ambientHome, StringComparison.OrdinalIgnoreCase), $"{variable} is the ambient home");
                Assert.False(value.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase) && !value.StartsWith(_stateRoot, StringComparison.OrdinalIgnoreCase),
                    $"{variable} points into the user's profile");
            }
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
            AssertNoSecret(outcome.Output, "the outcome output", Key, AmbientKey);
            AssertNoSecret(outcome.ErrorMessage, "the outcome error", Key, AmbientKey);
            AssertNoSecret(System.Text.Json.JsonSerializer.Serialize(outcome.Report), "the runtime report", Key, AmbientKey);
            Assert.Contains("[REDACTED]", outcome.Output);
            var logs = string.Join('\n', runtime == Runtime.ClaudeCode
                ? ((ListLogger<ClaudeCodeExecutionAdapter>)logger).Lines
                : ((ListLogger<CodexExecutionAdapter>)logger).Lines);
            AssertNoSecret(logs, "the logs", Key, AmbientKey);
            Assert.Contains("leaked", logs); // stderr was logged — redacted

            // workspace, runtime home, temp files
            Assert.Equal(before, Directory.GetFileSystemEntries(_workspace, "*", SearchOption.AllDirectories));
            AssertNoRuntimeStateLeft();
        }
        finally
        {
            // Restore what the host really had (the old code cleared these, discarding a developer's own values).
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
