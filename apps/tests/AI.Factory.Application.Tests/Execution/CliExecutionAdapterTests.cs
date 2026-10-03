using System.Diagnostics;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Tests.Gateway;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase M — the adapter contract for the Claude Code and Codex execution adapters, both behind the same
/// <see cref="CliExecutionAdapter"/> boundary. The CLI is replaced by a scripted <see cref="IProcessRunner"/>
/// that replays stdout exactly as the installed CLIs emit it (event shapes captured from Claude Code 2.1.121
/// and codex-cli 0.160.0), so every outcome class is deterministic: no process, no network, no spend.
/// </summary>
public sealed class CliExecutionAdapterTests : IDisposable
{
    private const string Key = "factory-selected-credential-0123456789";
    private const string RoutedModel = "factory/model-a";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aif-cli-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _stateRoot;

    public CliExecutionAdapterTests()
    {
        _workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        _stateRoot = Path.Combine(_root, "state");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Replays scripted stdout through OnStandardOutputLine and honours cancellation like the real runner.</summary>
    private sealed class ScriptedCli : IProcessRunner
    {
        private readonly Func<ProcessRunRequest, (string[] Lines, ProcessRunResult Result)> _script;

        public ScriptedCli(Func<ProcessRunRequest, (string[] Lines, ProcessRunResult Result)> script) => _script = script;

        public ProcessRunRequest? Request { get; private set; }

        public bool SandboxExistedDuringRun { get; private set; }

        public int Calls { get; private set; }

        public Exception? Throw { get; init; }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            SandboxExistedDuringRun = request.EnvironmentVariables is { } env && env.TryGetValue("HOME", out var home)
                && Directory.Exists(home);

            if (Throw is not null)
            {
                throw Throw;
            }

            using (var self = Process.GetCurrentProcess())
            {
                request.OnStarted?.Invoke(self);
            }

            var (lines, result) = _script(request);
            foreach (var line in lines)
            {
                request.OnStandardOutputLine?.Invoke(line);
                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result with { StandardOutput = string.Join("\n", lines) });
        }
    }

    public enum Runtime
    {
        ClaudeCode,
        Codex
    }

    private CliExecutionAdapter Create(Runtime runtime, IProcessRunner runner, string? secret = Key, bool mapModel = true)
    {
        var cli = new CliRuntimeOptions { StateRoot = _stateRoot };
        var secrets = new FakeSecretProvider(new Dictionary<string, string?>
        {
            ["ANTHROPIC_API_KEY"] = secret, ["OPENAI_API_KEY"] = secret
        });

        return runtime == Runtime.ClaudeCode
            ? new ClaudeCodeExecutionAdapter(
                new ClaudeCodeOptions { Models = mapModel ? new() { [RoutedModel] = "claude-haiku-4-5" } : new() },
                cli, runner, secrets, NullLogger<ClaudeCodeExecutionAdapter>.Instance)
            : new CodexExecutionAdapter(
                new CodexOptions { Models = mapModel ? new() { [RoutedModel] = "gpt-5-codex" } : new() },
                cli, runner, secrets, NullLogger<CodexExecutionAdapter>.Instance);
    }

    private AgentExecutionSpec Spec(string? workspace = "<ws>", string? model = RoutedModel, DateTimeOffset? deadline = null) => new(
        AgentRunId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        ProjectId: Guid.NewGuid(),
        WorkItemId: Guid.NewGuid(),
        Attempt: 1,
        Iteration: 0,
        Agent: new ExecutionAgent(Guid.NewGuid(), "coder", "Implementation", "SYSTEM PROMPT"),
        Instructions: "Do the work.",
        Workspace: new ExecutionWorkspace(workspace == "<ws>" ? _workspace : workspace),
        Model: new ModelRoute(model, "openrouter"),
        Timeout: TimeSpan.FromMinutes(5),
        Deadline: deadline);

    private static ProcessRunResult Exit(int code) => new(code, string.Empty, string.Empty, TimedOut: false);

    // ---- Event scripts, shaped exactly like the installed CLIs' output ----

    private static string[] ClaudeSuccess(bool withUsage = true, bool withCost = true, bool withSession = true) => new[]
    {
        "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-haiku-4-5\",\"permissionMode\":\"dontAsk\""
            + (withSession ? ",\"session_id\":\"s-1\"" : string.Empty) + "}",
        "{\"type\":\"assistant\",\"message\":{\"content\":[]}}",
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":3,\"result\":\"DONE\""
            + (withSession ? ",\"session_id\":\"s-1\"" : string.Empty)
            + (withCost ? ",\"total_cost_usd\":0.0123" : string.Empty)
            + (withUsage ? ",\"usage\":{\"input_tokens\":100,\"output_tokens\":20}" : string.Empty) + "}"
    };

    private static string[] CodexSuccess(bool withUsage = true, bool withSession = true) => new[]
    {
        withSession ? "{\"type\":\"thread.started\",\"thread_id\":\"t-1\"}" : "{\"type\":\"turn.started\"}",
        "{\"type\":\"item.completed\",\"item\":{\"id\":\"i0\",\"type\":\"error\",\"message\":\"Model metadata for `x` not found.\"}}",
        "{\"type\":\"item.completed\",\"item\":{\"id\":\"i1\",\"type\":\"agent_message\",\"text\":\"DONE\"}}",
        "{\"type\":\"turn.completed\"" + (withUsage ? ",\"usage\":{\"input_tokens\":100,\"cached_input_tokens\":10,\"output_tokens\":20}" : string.Empty) + "}"
    };

    private static string[] Success(Runtime runtime) => runtime == Runtime.ClaudeCode ? ClaudeSuccess() : CodexSuccess();

    // ---------------- Structured success / failure ----------------

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task StructuredSuccess_Succeeds_WithUsageSessionAndRuntimeReport(Runtime runtime)
    {
        var cli = new ScriptedCli(_ => (Success(runtime), Exit(0)));

        var outcome = await Create(runtime, cli).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Succeeded, outcome.Status);
        Assert.Equal("DONE", outcome.Output);
        Assert.Null(outcome.FailureCategory);
        Assert.Equal(new ExecutionUsage(100, 20, 120), outcome.Usage);
        Assert.Equal(runtime == Runtime.ClaudeCode ? "s-1" : "t-1", outcome.Report!.SessionId);
        Assert.Equal(runtime.ToString(), outcome.Report.Runtime);
        Assert.Equal(0, outcome.Report.ExitCode);
        Assert.NotNull(outcome.Report.StartedAt);
        Assert.Equal("exit:0", outcome.ExitReason);
    }

    [Fact]
    public async Task ClaudeCost_IsRecordedWithItsSource_WhenTheCliReportsIt()
    {
        var outcome = await Create(Runtime.ClaudeCode, new ScriptedCli(_ => (ClaudeSuccess(), Exit(0)))).ExecuteAsync(Spec());

        Assert.Equal(0.0123m, outcome.Report!.CostUsd);
        Assert.Equal("claude:total_cost_usd", outcome.Report.CostSource);
        Assert.Equal(3, outcome.Report.Turns);
        Assert.Equal("claude-haiku-4-5", outcome.Report.ReportedModel);
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)] // Codex never reports cost.
    public async Task CostUsageAndSession_StayNull_WhenTheCliDoesNotExposeThem(Runtime runtime)
    {
        var lines = runtime == Runtime.ClaudeCode
            ? ClaudeSuccess(withUsage: false, withCost: false, withSession: false)
            : CodexSuccess(withUsage: false, withSession: false);

        var outcome = await Create(runtime, new ScriptedCli(_ => (lines, Exit(0)))).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Succeeded, outcome.Status);
        Assert.Null(outcome.Usage);
        Assert.Null(outcome.Report!.CostUsd);
        Assert.Null(outcome.Report.SessionId);
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task StructuredFailure_ReportedByTheCli_Fails(Runtime runtime)
    {
        var lines = runtime == Runtime.ClaudeCode
            ? new[] { "{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"is_error\":true,\"result\":\"ran out of turns\",\"terminal_reason\":\"max_turns\"}" }
            : new[] { "{\"type\":\"thread.started\",\"thread_id\":\"t-2\"}", "{\"type\":\"turn.failed\",\"error\":{\"message\":\"stream disconnected, request id: req_abc123\"}}" };

        var outcome = await Create(runtime, new ScriptedCli(_ => (lines, Exit(1)))).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(FailureCategory.Agent, outcome.FailureCategory);
        Assert.Equal("exit:1", outcome.ExitReason);
        if (runtime == Runtime.Codex)
        {
            Assert.Equal("req_abc123", outcome.Report!.UpstreamRequestId); // printed by Codex, never synthesized
        }
    }

    /// <summary>Exit code 0 is never success on its own.</summary>
    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task ExitZero_WithAFailedStructuredResult_IsAFailure(Runtime runtime)
    {
        var lines = runtime == Runtime.ClaudeCode
            // Observed live: an unauthenticated run reports subtype "success" with is_error true.
            ? new[] { "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"something failed\"}" }
            : new[] { "{\"type\":\"turn.failed\",\"error\":{\"message\":\"boom\"}}" };

        var outcome = await Create(runtime, new ScriptedCli(_ => (lines, Exit(0)))).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode, 0)]
    [InlineData(Runtime.ClaudeCode, 1)]
    [InlineData(Runtime.Codex, 0)]
    [InlineData(Runtime.Codex, 1)]
    public async Task MissingOutput_IsNeverSuccess(Runtime runtime, int exitCode)
    {
        var outcome = await Create(runtime, new ScriptedCli(_ => (Array.Empty<string>(), Exit(exitCode)))).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Contains("success cannot be proven", outcome.ErrorMessage);
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task MalformedOutput_IsNeverSuccess(Runtime runtime)
    {
        var lines = new[] { "not json", "{\"type\":", "{}", "[1,2]", "{\"type\":\"result\"" };

        var outcome = await Create(runtime, new ScriptedCli(_ => (lines, Exit(0)))).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task NonZeroExit_EvenWithASuccessEvent_IsAFailure(Runtime runtime)
    {
        var outcome = await Create(runtime, new ScriptedCli(_ => (Success(runtime), Exit(2)))).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal("exit:2", outcome.ExitReason);
    }

    // ---------------- Launch, policy, timeout, cancellation, auth ----------------

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task MissingExecutable_FailsClearly_AndCleansUp(Runtime runtime)
    {
        var cli = new ScriptedCli(_ => (Array.Empty<string>(), Exit(0)))
        {
            Throw = new ProcessLaunchException("not found", new System.ComponentModel.Win32Exception(2))
        };

        var outcome = await Create(runtime, cli).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(FailureCategory.Validation, outcome.FailureCategory); // configuration fault: never retried
        Assert.Equal("launch_failed", outcome.ExitReason);
        Assert.Contains("ExecutablePath", outcome.ErrorMessage);
        AssertNoRuntimeStateLeft();
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task CommandRejectedByPolicy_Fails(Runtime runtime)
    {
        var cli = new ScriptedCli(_ => (Array.Empty<string>(), Exit(0)))
        {
            Throw = new SecurityViolationException("Command 'x' is not in the allowed-commands list.")
        };

        var outcome = await Create(runtime, cli).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal("security_violation", outcome.ExitReason);
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task Timeout_IsTimedOut_AndCleansUp(Runtime runtime)
    {
        var cli = new ScriptedCli(_ => (Array.Empty<string>(), new ProcessRunResult(-1, "", "", TimedOut: true)));

        var outcome = await Create(runtime, cli).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.TimedOut, outcome.Status);
        Assert.Equal(FailureCategory.Timeout, outcome.FailureCategory);
        AssertNoRuntimeStateLeft();
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task FactoryDeadline_BoundsTheProcessTimeout(Runtime runtime)
    {
        var cli = new ScriptedCli(_ => (Success(runtime), Exit(0)));

        await Create(runtime, cli).ExecuteAsync(Spec(deadline: DateTimeOffset.UtcNow.AddSeconds(30)));

        Assert.True(cli.Request!.Timeout <= TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task ExpiredDeadline_NeverLaunches(Runtime runtime)
    {
        var cli = new ScriptedCli(_ => (Success(runtime), Exit(0)));

        var outcome = await Create(runtime, cli).ExecuteAsync(Spec(deadline: DateTimeOffset.UtcNow.AddSeconds(-1)));

        Assert.Equal(ExecutionOutcomeStatus.TimedOut, outcome.Status);
        Assert.Equal(0, cli.Calls);
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task Cancellation_IsCancelled_AndCleansUp(Runtime runtime)
    {
        using var cts = new CancellationTokenSource();
        var cli = new ScriptedCli(_ =>
        {
            cts.Cancel();
            return (new[] { "{\"type\":\"turn.started\"}" }, Exit(0));
        });

        var outcome = await Create(runtime, cli).ExecuteAsync(Spec(), cts.Token);

        Assert.Equal(ExecutionOutcomeStatus.Cancelled, outcome.Status);
        Assert.Equal(FailureCategory.Cancelled, outcome.FailureCategory);
        AssertNoRuntimeStateLeft();
    }

    /// <summary>Both CLIs retry a 401 for minutes (observed live); the adapter stops at the first one.</summary>
    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task AuthenticationFailure_AbortsImmediately_AsAProviderAuthenticationFailure(Runtime runtime)
    {
        var first = runtime == Runtime.ClaudeCode
            ? "{\"type\":\"system\",\"subtype\":\"api_retry\",\"attempt\":1,\"max_retries\":10,\"error_status\":401,\"error\":\"authentication_failed\"}"
            : "{\"type\":\"error\",\"message\":\"unexpected status 401 Unauthorized: Incorrect API key provided, request id: req_auth1, auth error code: invalid_api_key\"}";
        var cli = new ScriptedCli(_ => (new[] { first, "{\"type\":\"never-reached\"}" }, Exit(0)));

        var outcome = await Create(runtime, cli).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(FailureCategory.Provider, outcome.FailureCategory); // not retryable by the WorkItem budget
        Assert.Equal("Authentication", outcome.GatewayFailure);
        Assert.Equal("aborted", outcome.ExitReason);
        AssertNoRuntimeStateLeft();
    }

    // ---------------- Model and credential authority ----------------

    [Theory]
    [InlineData(Runtime.ClaudeCode, null)]
    [InlineData(Runtime.ClaudeCode, "auto")]
    [InlineData(Runtime.ClaudeCode, "some/unconfigured-model")]
    [InlineData(Runtime.Codex, null)]
    [InlineData(Runtime.Codex, "auto")]
    public async Task UnmappedOrMissingModel_FailsClosed_WithoutLaunching(Runtime runtime, string? model)
    {
        var cli = new ScriptedCli(_ => (Success(runtime), Exit(0)));

        var outcome = await Create(runtime, cli).ExecuteAsync(Spec(model: model));

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(FailureCategory.Validation, outcome.FailureCategory);
        Assert.Equal(0, cli.Calls);
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task MissingFactoryCredential_FailsClosed_WithoutLaunching(Runtime runtime)
    {
        var cli = new ScriptedCli(_ => (Success(runtime), Exit(0)));

        var outcome = await Create(runtime, cli, secret: null).ExecuteAsync(Spec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Contains("could not be resolved", outcome.ErrorMessage);
        Assert.Equal(0, cli.Calls);
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode, "ANTHROPIC_API_KEY", "CLAUDE_CONFIG_DIR")]
    [InlineData(Runtime.Codex, "CODEX_API_KEY", "CODEX_HOME")]
    public async Task FactoryCredential_ReachesOnlyTheChildEnvironment_ThroughAnIsolatedHome(
        Runtime runtime, string credentialVariable, string configVariable)
    {
        // Ambient credentials and CLI config in the Worker's own environment must not reach the child.
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "ambient-anthropic-key");
        Environment.SetEnvironmentVariable("CODEX_API_KEY", "ambient-codex-key");
        Environment.SetEnvironmentVariable("AIF_TEST_SECRET_TOKEN", "ambient-secret");
        try
        {
            var cli = new ScriptedCli(_ => (Success(runtime), Exit(0)));
            await Create(runtime, cli).ExecuteAsync(Spec());
            var request = cli.Request!;
            var env = request.EnvironmentVariables!;

            Assert.Equal(Key, env[credentialVariable]);
            Assert.DoesNotContain(env.Values, v => v.StartsWith("ambient-", StringComparison.Ordinal));
            Assert.DoesNotContain(env.Keys, k => k == "AIF_TEST_SECRET_TOKEN");
            Assert.DoesNotContain(request.Arguments, a => a.Contains(Key, StringComparison.Ordinal)); // never argv
            Assert.DoesNotContain(Key, request.StandardInput);

            var home = env[configVariable];
            Assert.StartsWith(Path.GetFullPath(_stateRoot), home, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(home, env["HOME"]);
            Assert.Equal(home, env["USERPROFILE"]);
            Assert.Equal(home, env["APPDATA"]);
            Assert.True(cli.SandboxExistedDuringRun);
            Assert.Equal(_workspace, request.WorkingDirectory); // exactly the Factory workspace
            AssertNoRuntimeStateLeft();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("CODEX_API_KEY", null);
            Environment.SetEnvironmentVariable("AIF_TEST_SECRET_TOKEN", null);
        }
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task CredentialEchoedByTheCli_IsRedacted(Runtime runtime)
    {
        var lines = runtime == Runtime.ClaudeCode
            ? new[] { $"{{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"key is {Key}\"}}" }
            : new[] { $"{{\"type\":\"item.completed\",\"item\":{{\"type\":\"agent_message\",\"text\":\"key is {Key}\"}}}}", "{\"type\":\"turn.completed\"}" };

        var outcome = await Create(runtime, new ScriptedCli(_ => (lines, Exit(0)))).ExecuteAsync(Spec());

        Assert.DoesNotContain(Key, outcome.Output);
        Assert.Contains("[REDACTED]", outcome.Output);
    }

    [Fact]
    public async Task ClaudeInvocation_IsNonInteractive_Isolated_AndPermissionBounded()
    {
        var cli = new ScriptedCli(_ => (ClaudeSuccess(), Exit(0)));
        var spec = Spec();

        await Create(Runtime.ClaudeCode, cli).ExecuteAsync(spec);
        var args = cli.Request!.Arguments.ToList();

        Assert.Equal("claude", cli.Request.FileName);
        Assert.Contains("-p", args);
        Assert.Equal("stream-json", args[args.IndexOf("--output-format") + 1]);
        Assert.Equal("claude-haiku-4-5", args[args.IndexOf("--model") + 1]); // the mapped model, never "auto"
        Assert.Equal(spec.AgentRunId.ToString("D"), args[args.IndexOf("--session-id") + 1]);
        Assert.Equal("dontAsk", args[args.IndexOf("--permission-mode") + 1]);
        Assert.Equal(string.Empty, args[args.IndexOf("--setting-sources") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Contains("--bare", args); // API-key auth: strictly that key, no OAuth/keychain
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.DoesNotContain("bypassPermissions", args);
        Assert.Equal("SYSTEM PROMPT\n\nDo the work.", cli.Request.StandardInput); // prompt via stdin, not argv
    }

    [Fact]
    public async Task ClaudeWithASubscriptionToken_DoesNotUseBare_AndInjectsTheOauthVariable()
    {
        var cli = new ScriptedCli(_ => (ClaudeSuccess(), Exit(0)));
        var adapter = new ClaudeCodeExecutionAdapter(
            new ClaudeCodeOptions
            {
                CredentialSecretName = "CLAUDE_CODE_OAUTH_TOKEN",
                CredentialEnvironmentVariable = "CLAUDE_CODE_OAUTH_TOKEN",
                Models = new() { [RoutedModel] = "claude-haiku-4-5" }
            },
            new CliRuntimeOptions { StateRoot = _stateRoot }, cli,
            new FakeSecretProvider(new Dictionary<string, string?> { ["CLAUDE_CODE_OAUTH_TOKEN"] = Key }),
            NullLogger<ClaudeCodeExecutionAdapter>.Instance);

        await adapter.ExecuteAsync(Spec());

        Assert.DoesNotContain("--bare", cli.Request!.Arguments);
        Assert.Equal(Key, cli.Request.EnvironmentVariables!["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.False(cli.Request.EnvironmentVariables.ContainsKey("ANTHROPIC_API_KEY"));
    }

    [Fact]
    public async Task CodexInvocation_IsNonInteractive_Isolated_AndSandboxed()
    {
        var cli = new ScriptedCli(_ => (CodexSuccess(), Exit(0)));

        await Create(Runtime.Codex, cli).ExecuteAsync(Spec());
        var args = cli.Request!.Arguments.ToList();

        Assert.Equal("exec", args[0]);
        Assert.Contains("--json", args);
        Assert.Contains("--ephemeral", args);
        Assert.Contains("--ignore-user-config", args);
        Assert.Contains("--ignore-rules", args);
        Assert.Equal("approval_policy=\"never\"", args[args.IndexOf("-c") + 1]);
        Assert.Equal("workspace-write", args[args.IndexOf("-s") + 1]);
        Assert.Equal("gpt-5-codex", args[args.IndexOf("-m") + 1]);
        Assert.Equal(_workspace, args[args.IndexOf("-C") + 1]);
        Assert.Equal("-", args[^1]); // prompt from stdin
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", args);
        Assert.DoesNotContain("danger-full-access", args);
    }

    [Fact]
    public void CodexOptions_RefuseABroaderSandbox()
    {
        Assert.Throws<InvalidOperationException>(() => new CodexOptions { Sandbox = "danger-full-access" }.Validate());
        new CodexOptions().Validate();
    }

    [Fact]
    public void ClaudeOptions_AcceptOnlyTheTwoSupportedCredentialVariables()
    {
        Assert.Throws<InvalidOperationException>(() => new ClaudeCodeOptions { CredentialEnvironmentVariable = "MY_KEY" }.Validate());
        new ClaudeCodeOptions().Validate();
        new ClaudeCodeOptions { CredentialEnvironmentVariable = "CLAUDE_CODE_OAUTH_TOKEN" }.Validate();
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task MissingWorkspace_FailsWithoutLaunching(Runtime runtime)
    {
        var cli = new ScriptedCli(_ => (Success(runtime), Exit(0)));

        var outcome = await Create(runtime, cli).ExecuteAsync(Spec(workspace: Path.Combine(_root, "nope")));

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, cli.Calls);
    }

    [Fact]
    public async Task PlannerRun_WithoutAFactoryWorkspace_RunsInItsDisposableScratch()
    {
        var cli = new ScriptedCli(_ => (ClaudeSuccess(), Exit(0)));

        await Create(Runtime.ClaudeCode, cli).ExecuteAsync(Spec(workspace: null));

        Assert.StartsWith(Path.GetFullPath(_stateRoot), cli.Request!.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        AssertNoRuntimeStateLeft();
    }

    [Fact]
    public async Task WorkspaceInsideTheRuntimeStateRoot_IsRefused()
    {
        var inside = Directory.CreateDirectory(Path.Combine(_stateRoot, "ws")).FullName;
        var cli = new ScriptedCli(_ => (ClaudeSuccess(), Exit(0)));

        var outcome = await Create(Runtime.ClaudeCode, cli).ExecuteAsync(Spec(workspace: inside));

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, cli.Calls);
    }

    [Theory]
    [InlineData(Runtime.ClaudeCode)]
    [InlineData(Runtime.Codex)]
    public async Task RuntimeStateNeverLandsInTheWorkspace(Runtime runtime)
    {
        var before = Directory.GetFileSystemEntries(_workspace, "*", SearchOption.AllDirectories);

        await Create(runtime, new ScriptedCli(_ => (Success(runtime), Exit(0)))).ExecuteAsync(Spec());

        Assert.Equal(before, Directory.GetFileSystemEntries(_workspace, "*", SearchOption.AllDirectories));
    }

    private void AssertNoRuntimeStateLeft() =>
        Assert.True(!Directory.Exists(_stateRoot) || Directory.GetFileSystemEntries(_stateRoot).Length == 0,
            "the per-run CLI state directory must be removed when the run ends");
}
