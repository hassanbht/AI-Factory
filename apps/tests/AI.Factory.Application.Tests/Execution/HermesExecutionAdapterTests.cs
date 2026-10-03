using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Execution-adapter Phase C — <see cref="HermesExecutionAdapter"/>, ported from the
/// former HermesAgentRuntimeTests and LegacyHermesRuntimeAdapterTests, plus the
/// Hermes mechanics underneath it. Two layers:
/// <list type="bullet">
/// <item>adapter over a stub <see cref="IAgentExecutor"/>: request construction and
/// outcome/exit-reason mapping;</item>
/// <item>adapter over the real <see cref="HermesAgentExecutor"/> with a fake
/// <see cref="IProcessRunner"/>: the docker command, environment and API-key
/// injection, truncation and exit codes that actually reach the process layer.</item>
/// </list>
/// No process, container or model is ever started.
/// </summary>
public sealed class HermesExecutionAdapterTests
{
    private sealed class StubExecutor : IAgentExecutor
    {
        private readonly Func<AgentExecutionRequest, CancellationToken, AgentExecutionResult> _respond;

        public StubExecutor(Func<AgentExecutionRequest, CancellationToken, AgentExecutionResult> respond) => _respond = respond;

        public AgentExecutionRequest? LastRequest { get; private set; }

        public int CallCount { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(_respond(request, cancellationToken));
        }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Func<ProcessRunRequest, ProcessRunResult> _respond;

        public FakeProcessRunner(Func<ProcessRunRequest, ProcessRunResult> respond) => _respond = respond;

        public ProcessRunRequest? LastRequest { get; private set; }
        public ProcessRunRequest? RunRequest { get; private set; }
        public List<ProcessRunRequest> AllRequests { get; } = new();

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            AllRequests.Add(request);
            if (request.Arguments.Contains("run"))
            {
                RunRequest = request;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = _respond(request);
            if (request.Arguments.Contains("run") && !result.TimedOut)
            {
                HermesRunReportFake.Write(request);
            }

            return Task.FromResult(result);
        }
    }

    private static AgentExecutionSpec Spec(
        string? model = "m-1",
        string? gateway = "gw-1",
        string? workspace = null,
        string systemPrompt = "SYSTEM",
        string? instructions = "INSTRUCTIONS") => new(
        AgentRunId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        ProjectId: null,
        WorkItemId: null,
        Attempt: 1,
        Iteration: 0,
        Agent: new ExecutionAgent(Guid.NewGuid(), "Coder", "Implementation", systemPrompt),
        Instructions: instructions,
        Workspace: new ExecutionWorkspace(workspace),
        Model: new ModelRoute(model, gateway));

    private static HermesExecutionAdapter Adapter(IAgentExecutor executor) =>
        new(executor, new FailureClassifier(), NullLogger<HermesExecutionAdapter>.Instance);

    private static async Task<(ExecutionOutcome Outcome, StubExecutor Executor)> ExecuteAsync(
        AgentExecutionSpec spec, Func<AgentExecutionRequest, CancellationToken, AgentExecutionResult> respond)
    {
        var executor = new StubExecutor(respond);
        return (await Adapter(executor).ExecuteAsync(spec), executor);
    }

    // ---- identity ----------------------------------------------------------

    [Fact]
    public void Identity_IsTheHermesKey_WithFactoryRoutedModelCapability()
    {
        Assert.Equal("Hermes", HermesExecutionAdapter.AdapterKey);
        var adapter = Adapter(new StubExecutor((_, _) => throw new InvalidOperationException()));
        Assert.Equal("Hermes", adapter.Key);
        Assert.Equal(ExecutionCapabilities.FactoryRoutedModel, adapter.Capabilities);
    }

    // ---- request construction ---------------------------------------------

    [Fact]
    public async Task Prompt_IsSystemPromptThenInstructions_AgentIsTheDefinitionName()
    {
        var (_, executor) = await ExecuteAsync(Spec(), (_, _) => new AgentExecutionResult(true, "ok", null, 1));

        Assert.Equal("SYSTEM\n\nINSTRUCTIONS", executor.LastRequest!.Prompt);
        Assert.Equal("Coder", executor.LastRequest.Agent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Prompt_WithoutSystemPrompt_IsTheInstructionsAlone(string systemPrompt)
    {
        var (_, executor) = await ExecuteAsync(
            Spec(systemPrompt: systemPrompt), (_, _) => new AgentExecutionResult(true, "ok", null, 1));

        Assert.Equal("INSTRUCTIONS", executor.LastRequest!.Prompt);
    }

    [Fact]
    public async Task Prompt_WithoutSystemPromptOrInstructions_IsEmpty()
    {
        var (_, executor) = await ExecuteAsync(
            Spec(systemPrompt: "", instructions: null), (_, _) => new AgentExecutionResult(true, "ok", null, 1));

        Assert.Equal(string.Empty, executor.LastRequest!.Prompt);
    }

    [Fact]
    public async Task RoutedModel_ReachesHermes_AndTheRouteIsReportedBack()
    {
        var (outcome, executor) = await ExecuteAsync(
            Spec(model: "routed-model", gateway: "routed-gateway"), (_, _) => new AgentExecutionResult(true, "ok", null, 1));

        Assert.Equal("routed-model", executor.LastRequest!.Model);
        Assert.Equal(new ModelRoute("routed-model", "routed-gateway"), outcome.Model);
    }

    [Fact]
    public async Task NoRoutedModel_ReachesHermesAsNull_SoHermesOptionsDefaultModelApplies()
    {
        var (outcome, executor) = await ExecuteAsync(
            Spec(model: null, gateway: null), (_, _) => new AgentExecutionResult(true, "ok", null, 1));

        Assert.Null(executor.LastRequest!.Model);
        Assert.Equal(new ModelRoute(null, null), outcome.Model);
    }

    [Fact]
    public async Task ResolvedWorkspace_IsTheWorkingDirectory()
    {
        var (_, executor) = await ExecuteAsync(
            Spec(workspace: "/ws/project"), (_, _) => new AgentExecutionResult(true, "ok", null, 1, "/ws/project"));

        Assert.Equal("/ws/project", executor.LastRequest!.WorkingDirectory);
    }

    [Fact]
    public async Task NoWorkspace_ReachesHermesAsEmptyWorkingDirectory()
    {
        var (_, executor) = await ExecuteAsync(Spec(workspace: null), (_, _) => new AgentExecutionResult(true, "ok", null, 1));

        Assert.Equal(string.Empty, executor.LastRequest!.WorkingDirectory);
    }

    [Fact]
    public async Task CancellationToken_IsPassedToTheExecutor()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;
        var executor = new StubExecutor((_, ct) =>
        {
            seen = ct;
            return new AgentExecutionResult(true, "ok", null, 1);
        });

        await Adapter(executor).ExecuteAsync(Spec(), cts.Token);

        Assert.Equal(cts.Token, seen);
    }

    // ---- outcome mapping ---------------------------------------------------

    [Fact]
    public async Task Success_MapsToSucceeded_WithOutputDurationAndNoInventedTelemetry()
    {
        var (outcome, _) = await ExecuteAsync(
            Spec(), (_, _) => new AgentExecutionResult(true, "hello world", null, 42, "/workspace/x", ExitCode: 0));

        Assert.Equal(ExecutionOutcomeStatus.Succeeded, outcome.Status);
        Assert.Equal("hello world", outcome.Output);
        Assert.Null(outcome.ErrorMessage);
        Assert.Null(outcome.FailureCategory);
        Assert.Equal(42, outcome.DurationMs);
        Assert.Null(outcome.Usage);
        Assert.Empty(outcome.ArtifactReferences);
        Assert.Empty(outcome.ToolCallNames);
        Assert.Equal("exit:0", outcome.ExitReason);
    }

    [Fact]
    public async Task Success_WithoutAReportedExitCode_IsExit0_BecauseHermesSuccessMeansExitCode0()
    {
        var (outcome, _) = await ExecuteAsync(Spec(), (_, _) => new AgentExecutionResult(true, "ok", null, 1));

        Assert.Equal("exit:0", outcome.ExitReason);
    }

    [Fact]
    public async Task NonZeroExit_MapsToFailed_WithAgentCategory_AndExitCode()
    {
        var (outcome, _) = await ExecuteAsync(
            Spec(), (_, _) => new AgentExecutionResult(false, "partial", "Process exited with code 1.", 15, ExitCode: 1));

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(FailureCategory.Agent, outcome.FailureCategory);
        Assert.Equal("Process exited with code 1.", outcome.ErrorMessage);
        Assert.Equal("partial", outcome.Output);
        Assert.Equal(15, outcome.DurationMs);
        Assert.Equal("exit:1", outcome.ExitReason);
    }

    [Fact]
    public async Task FailureWithoutProcessExit_IsError_AndGetsTheDefaultMessageWhenNoneWasGiven()
    {
        var (outcome, _) = await ExecuteAsync(Spec(), (_, _) => new AgentExecutionResult(false, null, null, 3));

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(FailureCategory.Agent, outcome.FailureCategory);
        Assert.Equal("Agent execution failed with no further detail.", outcome.ErrorMessage);
        Assert.Equal("error", outcome.ExitReason);
    }

    [Theory]
    [InlineData("Execution timed out after 300 seconds.")]
    [InlineData("Operation timeout exceeded.")]
    public async Task TimeoutErrorText_MapsToTimedOut_WithTimeoutCategory(string error)
    {
        var (outcome, _) = await ExecuteAsync(
            Spec(), (_, _) => new AgentExecutionResult(false, "stdout before timeout", error, 300_000));

        Assert.Equal(ExecutionOutcomeStatus.TimedOut, outcome.Status);
        Assert.Equal(FailureCategory.Timeout, outcome.FailureCategory);
        Assert.Equal(error, outcome.ErrorMessage);
        Assert.Equal("stdout before timeout", outcome.Output);
        Assert.Equal(300_000, outcome.DurationMs);
        Assert.Equal("timeout", outcome.ExitReason);
    }

    [Fact]
    public async Task CancelledExecution_MapsToCancelled_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var executor = new StubExecutor((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return new AgentExecutionResult(true, "unreachable", null, 1);
        });

        var outcome = await Adapter(executor).ExecuteAsync(Spec(), cts.Token);

        Assert.Equal(ExecutionOutcomeStatus.Cancelled, outcome.Status);
        Assert.Equal(FailureCategory.Cancelled, outcome.FailureCategory);
        Assert.Equal("Agent execution was cancelled.", outcome.ErrorMessage);
        Assert.Null(outcome.Output);
        Assert.Equal("cancelled", outcome.ExitReason);
    }

    [Fact]
    public async Task OperationCanceled_WithoutTheRunTokenCancelled_Propagates()
    {
        var executor = new StubExecutor((_, _) => throw new OperationCanceledException("not ours"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => Adapter(executor).ExecuteAsync(Spec()));
    }

    [Fact]
    public async Task ExecutorException_Propagates()
    {
        var executor = new StubExecutor((_, _) => throw new InvalidOperationException("executor exploded"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Adapter(executor).ExecuteAsync(Spec()));
        Assert.Equal("executor exploded", ex.Message);
    }

    // ---- through the real HermesAgentExecutor (fake process runner) --------

    private static HermesOptions HermesOptions(Action<HermesOptions>? configure = null)
    {
        var options = new HermesOptions
        {
            DockerImage = "test/hermes:pinned",
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-hermes-adapter-tests"),
            TimeoutSeconds = 60,
            MaxOutputLength = 100_000,
            NetworkMode = "bridge"
        };
        configure?.Invoke(options);
        return options;
    }

    private static async Task<(ExecutionOutcome Outcome, ProcessRunRequest Process)> ExecuteThroughHermesAsync(
        AgentExecutionSpec spec,
        ProcessRunResult processResult,
        HermesOptions? options = null,
        ISecretProvider? secretProvider = null)
    {
        var runner = new FakeProcessRunner(_ => processResult);
        var executor = new HermesAgentExecutor(
            options ?? HermesOptions(), runner, NullLogger<HermesAgentExecutor>.Instance, secretProvider: secretProvider);

        var outcome = await Adapter(executor).ExecuteAsync(spec);
        return (outcome, runner.RunRequest ?? runner.LastRequest!);
    }

    [Fact]
    public async Task DockerCommand_IsTheUnchangedHermesInvocation()
    {
        var workspace = Directory.CreateTempSubdirectory("hermes-adapter-ws-").FullName;

        try
        {
            var options = HermesOptions(o =>
            {
                o.DockerImage = "test/hermes:pinned";
                o.Provider = "openrouter";
            });
            var spec = Spec(model: "routed-model", workspace: workspace);
            var (_, process) = await ExecuteThroughHermesAsync(
                spec, new ProcessRunResult(0, "ok", "", TimedOut: false), options);

            Assert.Equal("docker", process.FileName);
            Assert.Equal(workspace, process.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(60), process.Timeout);
            Assert.Equal(
                new[]
                {
                    "run", "--rm",
                    "--name", $"ai-factory-agent-{spec.AgentRunId:N}",
                    "--label", "factory=ai-factory",
                    "--label", $"agent-run-id={spec.AgentRunId}",
                    "--label", "adapter=Hermes",
                    "--label", "managed=true",
                    "--network", "bridge",
                    // Phase M — per-run anonymous Hermes home, never a configured host path.
                    "-v", "/opt/data",
                    "-v", $"{workspace}:/workspace",
                    "-e", "HERMES_WRITE_SAFE_ROOT=/workspace",
                    "test/hermes:pinned",
                    "-z", "SYSTEM\n\nINSTRUCTIONS",
                    // Phase J — Hermes' machine-readable run report (see HermesAgentExecutor).
                    "--usage-file", $"/workspace/{HermesAgentExecutor.RunReportFileName(spec.AgentRunId)}",
                    "-m", "routed-model",
                    "--provider", "openrouter"
                },
                process.Arguments);

            // The host docker process only ever inherits PATH.
            Assert.All(process.EnvironmentVariables!.Keys, key => Assert.Equal("PATH", key));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task NoRoutedModel_UsesHermesDefaultModel_AndNoProviderFlagWhenUnset()
    {
        var options = HermesOptions(o => o.DefaultModel = "hermes-default");

        var (_, process) = await ExecuteThroughHermesAsync(
            Spec(model: null), new ProcessRunResult(0, "ok", "", TimedOut: false), options);

        var args = process.Arguments.ToList();
        Assert.Equal("hermes-default", args[args.IndexOf("-m") + 1]);
        Assert.DoesNotContain("--provider", args);
    }

    [Fact]
    public async Task Environment_ConfiguredVariablesAndProviderApiKey_AreInjectedIntoTheContainer()
    {
        var options = HermesOptions(o =>
        {
            o.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = "http://gateway.internal/v1";
            o.AdditionalEnvironmentVariables["HERMES_WRITE_SAFE_ROOT"] = "/custom";
            o.ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY";
            o.ProviderApiKeySecretName = "HERMES_PROVIDER_KEY";
        });
        var secrets = new InMemorySecretProvider(new Dictionary<string, string> { ["HERMES_PROVIDER_KEY"] = "test-key-value" });

        var (_, process) = await ExecuteThroughHermesAsync(
            Spec(), new ProcessRunResult(0, "ok", "", TimedOut: false), options, secrets);

        var envFlags = process.Arguments
            .Select((arg, i) => (arg, i))
            .Where(x => x.arg == "-e")
            .Select(x => process.Arguments[x.i + 1])
            .ToArray();

        // Phase K — the key is passed by NAME (`-e OPENROUTER_API_KEY`); docker copies the value
        // from its own controlled environment, so it never appears on the command line.
        Assert.Equal(
            new[] { "OPENROUTER_BASE_URL=http://gateway.internal/v1", "HERMES_WRITE_SAFE_ROOT=/custom", "OPENROUTER_API_KEY" },
            envFlags);
        Assert.DoesNotContain(process.Arguments, a => a.Contains("test-key-value", StringComparison.Ordinal));
        Assert.Equal("test-key-value", process.EnvironmentVariables!["OPENROUTER_API_KEY"]);
        Assert.Equal(new[] { "OPENROUTER_API_KEY", "PATH" }, process.EnvironmentVariables.Keys.Order().ToArray());
    }

    [Fact]
    public async Task OversizedOutput_IsTruncatedByHermes_BeforeReachingTheOutcome()
    {
        var options = HermesOptions(o => o.MaxOutputLength = 10);

        var (outcome, _) = await ExecuteThroughHermesAsync(
            Spec(), new ProcessRunResult(0, new string('x', 50), "", TimedOut: false), options);

        Assert.Equal(new string('x', 10) + "...[truncated]", outcome.Output);
    }

    [Fact]
    public async Task ProcessExitCode_BecomesTheExitReason()
    {
        var (outcome, _) = await ExecuteThroughHermesAsync(Spec(), new ProcessRunResult(3, "out", "", TimedOut: false));

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(FailureCategory.Agent, outcome.FailureCategory);
        Assert.Equal("Process exited with code 3.", outcome.ErrorMessage);
        Assert.Equal("exit:3", outcome.ExitReason);
    }

    [Fact]
    public async Task ProcessStderr_IsTheErrorMessage_WithTheExitCodeStillReported()
    {
        var (outcome, _) = await ExecuteThroughHermesAsync(Spec(), new ProcessRunResult(1, "", "fatal: boom", TimedOut: false));

        Assert.Equal("fatal: boom", outcome.ErrorMessage);
        Assert.Equal("exit:1", outcome.ExitReason);
    }

    [Fact]
    public async Task ProcessSuccess_IsExit0()
    {
        var (outcome, _) = await ExecuteThroughHermesAsync(Spec(), new ProcessRunResult(0, "done", "", TimedOut: false));

        Assert.Equal(ExecutionOutcomeStatus.Succeeded, outcome.Status);
        Assert.Equal("done", outcome.Output);
        Assert.Equal("exit:0", outcome.ExitReason);
    }

    [Fact]
    public async Task ProcessTimeout_IsTimedOut_WithHermesTimeoutMessage()
    {
        var (outcome, _) = await ExecuteThroughHermesAsync(Spec(), new ProcessRunResult(-1, "partial", "", TimedOut: true));

        Assert.Equal(ExecutionOutcomeStatus.TimedOut, outcome.Status);
        Assert.Equal(FailureCategory.Timeout, outcome.FailureCategory);
        Assert.Equal("Execution timed out after 60 seconds.", outcome.ErrorMessage);
        Assert.Equal("partial", outcome.Output);
        Assert.Equal("timeout", outcome.ExitReason);
    }

    [Fact]
    public async Task MissingResolvedWorkspace_FailsWithoutStartingDocker_AsError()
    {
        var missing = Path.Combine(Path.GetTempPath(), "hermes-adapter-missing-" + Guid.NewGuid().ToString("N"));
        var runner = new FakeProcessRunner(_ => throw new InvalidOperationException("docker must not start"));
        var executor = new HermesAgentExecutor(HermesOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        var outcome = await Adapter(executor).ExecuteAsync(Spec(workspace: missing));

        Assert.Null(runner.LastRequest);
        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Contains("no longer exists", outcome.ErrorMessage);
        Assert.Equal("error", outcome.ExitReason);
    }
}
