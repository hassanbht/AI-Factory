using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.OpenHands;
using AI.Factory.Infrastructure.Gateways;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

public sealed class OpenHandsExecutionAdapterTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = new();

        /// <summary>Exit code of `docker run` (the container launch); everything else exits 0.</summary>
        public int LaunchExitCode { get; init; }

        /// <summary>Phase O — the launch client never returns until the run is cancelled (it is then killed, as SystemProcessRunner does).</summary>
        public bool LaunchBlocksUntilCancelled { get; init; }

        /// <summary>Phase O — the launch client hits its own process timeout (reported as TimedOut, exit -1).</summary>
        public bool LaunchTimesOut { get; init; }

        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var isLaunch = request.Arguments.FirstOrDefault() == "run";

            if (isLaunch && LaunchBlocksUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (isLaunch && LaunchTimesOut)
            {
                return new ProcessRunResult(-1, string.Empty, string.Empty, TimedOut: true);
            }

            var exitCode = isLaunch ? LaunchExitCode : 0;
            return new ProcessRunResult(exitCode, string.Empty, exitCode == 0 ? string.Empty : "port is already allocated", false);
        }
    }

    private sealed class FakeLifecycleManager : IContainerLifecycleManager
    {
        public List<Guid> CleanedRuns { get; } = new();

        /// <summary>Phase O — runs whose cleanup was the bounded reconciliation (an interrupted launch), not the single-shot removal.</summary>
        public List<Guid> ReconciledRuns { get; } = new();

        public Task<ContainerCleanupOutcome> ReconcileCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default)
        {
            ReconciledRuns.Add(agentRunId);
            CleanedRuns.Add(agentRunId);
            return Task.FromResult(ContainerCleanupOutcome.Removed);
        }

        public Task EnsureCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default)
        {
            CleanedRuns.Add(agentRunId);
            return Task.CompletedTask;
        }

        public Task<int> SweepOrphanContainersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class FakeOpenHandsClient : IOpenHandsClient
    {
        public string ConversationId { get; set; } = "fake-conv-123";
        public Queue<string> ExecutionStatuses { get; } = new();
        public string? FinalResponse { get; set; } = "Done from Fake OpenHands";
        public bool Interrupted { get; private set; }
        public bool Deleted { get; private set; }
        public OpenHandsCreateConversationRequest? LastCreateRequest { get; private set; }
        public string? LastMessageSent { get; private set; }
        public OpenHandsStatsDto? Stats { get; set; }
        public Exception? CreateFailure { get; set; }

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<OpenHandsConversationInfo> CreateConversationAsync(
            OpenHandsCreateConversationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
            if (CreateFailure is not null)
            {
                throw CreateFailure;
            }

            return Task.FromResult(new OpenHandsConversationInfo
            {
                Id = ConversationId,
                ExecutionStatus = "idle"
            });
        }

        public Task<bool> SendMessageAsync(
            string conversationId,
            string message,
            bool run = true,
            CancellationToken cancellationToken = default)
        {
            LastMessageSent = message;
            return Task.FromResult(true);
        }

        public Task<OpenHandsConversationInfo> GetConversationAsync(
            string conversationId,
            CancellationToken cancellationToken = default)
        {
            var status = ExecutionStatuses.Count > 0 ? ExecutionStatuses.Dequeue() : "finished";
            return Task.FromResult(new OpenHandsConversationInfo
            {
                Id = conversationId,
                ExecutionStatus = status,
                Stats = Stats
            });
        }

        public Task<string?> GetFinalResponseAsync(
            string conversationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(FinalResponse);

        public Task<bool> InterruptAsync(
            string conversationId,
            CancellationToken cancellationToken = default)
        {
            Interrupted = true;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteConversationAsync(
            string conversationId,
            CancellationToken cancellationToken = default)
        {
            Deleted = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeClientFactory : IOpenHandsClientFactory
    {
        public FakeOpenHandsClient Client { get; } = new();

        public IOpenHandsClient CreateClient(string serverUrl, string? sessionApiKey) => Client;
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        public string ProviderName => "Fake";

        public Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fake-test-key-12345");

        public Task<SecretResolutionResult> ResolveSecretAsync(string secretName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretResolutionResult(true, "fake-test-key-12345", "Fake", DateTime.UtcNow));
    }

    private static AgentExecutionSpec CreateSpec(
        string? model = "deepseek/deepseek-v4-flash-0731",
        string? gateway = "OpenRouter",
        DateTimeOffset? deadline = null,
        string instructions = "Write a test file.") => new(
        AgentRunId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        ProjectId: Guid.NewGuid(),
        WorkItemId: Guid.NewGuid(),
        Attempt: 1,
        Iteration: 0,
        Agent: new ExecutionAgent(Guid.NewGuid(), "Coder", "Implementation", "You are a coding assistant."),
        Instructions: instructions,
        Workspace: new ExecutionWorkspace(Path.GetTempPath()),
        Model: new ModelRoute(model, gateway),
        Deadline: deadline);

    /// <summary>The real gateway descriptors behind a resolver, as the composition roots wire them.</summary>
    private static ModelGatewayResolver Resolver() => new(
        new ModelGatewayRegistry(new AI.Factory.Application.Abstractions.Gateway.IModelGateway[] { new OpenRouterGateway(), new OmniRouteGateway() }),
        new ModelGatewayOptions
        {
            DefaultGateway = OpenRouterGateway.Key,
            Gateways =
            {
                [OpenRouterGateway.Key] = new GatewayEntryOptions { BaseUrl = OpenRouterGateway.DefaultBaseUrl },
                [OmniRouteGateway.Key] = new GatewayEntryOptions { BaseUrl = OmniRouteGateway.DefaultBaseUrl, Enabled = false }
            }
        });

    private static OpenHandsExecutionAdapter RoutedAdapter(FakeClientFactory clientFactory, ModelGatewayResolver? resolver) => new(
        new OpenHandsOptions { BaseUrl = "http://127.0.0.1:48080", PollIntervalSeconds = 0.01 },
        new FakeProcessRunner(),
        new FakeLifecycleManager(),
        new FailureClassifier(),
        NullLogger<OpenHandsExecutionAdapter>.Instance,
        clientFactory: clientFactory,
        secretProvider: new FakeSecretProvider(),
        gatewayResolver: resolver);

    [Fact]
    public async Task ExecuteAsync_SendsTheRoutedGatewaysEndpointModelRouteAndKey_ToTheAgentServer()
    {
        var clientFactory = new FakeClientFactory();
        clientFactory.Client.ExecutionStatuses.Enqueue("running");
        clientFactory.Client.ExecutionStatuses.Enqueue("finished");

        var outcome = await RoutedAdapter(clientFactory, Resolver()).ExecuteAsync(CreateSpec());

        Assert.Equal(ExecutionOutcomeStatus.Succeeded, outcome.Status);
        var llm = clientFactory.Client.LastCreateRequest!.AgentSettings.Llm;
        Assert.Equal("openrouter/deepseek/deepseek-v4-flash-0731", llm.Model);
        Assert.Equal(OpenRouterGateway.DefaultBaseUrl, llm.BaseUrl);
        Assert.Equal("fake-test-key-12345", llm.ApiKey);
    }

    [Theory]
    [InlineData("OmniRoute", "is disabled")]
    [InlineData("NoSuchGateway", "is not registered")]
    public async Task ExecuteAsync_UnroutableGateway_FailsClosed_BeforeAnyConversation(string gateway, string reason)
    {
        var clientFactory = new FakeClientFactory();

        var outcome = await RoutedAdapter(clientFactory, Resolver()).ExecuteAsync(CreateSpec(gateway: gateway));

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.StartsWith("Model gateway route rejected:", outcome.ErrorMessage);
        Assert.Contains(reason, outcome.ErrorMessage);
        Assert.Null(clientFactory.Client.LastCreateRequest);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutAGatewayResolver_FailsClosed_NoHardcodedProviderFallback()
    {
        var clientFactory = new FakeClientFactory();

        var outcome = await RoutedAdapter(clientFactory, resolver: null).ExecuteAsync(CreateSpec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Contains("requires a model gateway resolver", outcome.ErrorMessage);
        Assert.Null(clientFactory.Client.LastCreateRequest);
    }

    [Theory]
    [InlineData("sk-or-v1-0123456789abcdef0123456789abcdef")]
    [InlineData("omni-7f3a9c2e5b8d1f4a6c0e9b2d")]
    public async Task ExecuteAsync_AgentServerErrorEchoingTheApiKey_IsRedactedAndBounded(string key)
    {
        var clientFactory = new FakeClientFactory();
        clientFactory.Client.CreateFailure = new HttpRequestException(
            "OpenHands conversation creation failed: UnprocessableEntity {\"detail\":[{\"input\":{\"model\":\"x\",\"api_key\":\""
                + key + "\"}}]} " + new string('x', 2_000));

        var outcome = await RoutedAdapter(clientFactory, Resolver()).ExecuteAsync(CreateSpec());

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.DoesNotContain(key, outcome.ErrorMessage);
        Assert.Contains("OpenHands conversation creation failed", outcome.ErrorMessage);
        Assert.True(outcome.ErrorMessage!.Length <= 600);
        Assert.Null(outcome.GatewayFailure);
    }

    [Fact]
    public void Identity_HasOpenHandsKey_AndFactoryRoutedModelCapability()
    {
        var adapter = new OpenHandsExecutionAdapter(
            new OpenHandsOptions(),
            new FakeProcessRunner(),
            new FakeLifecycleManager(),
            new FailureClassifier(),
            NullLogger<OpenHandsExecutionAdapter>.Instance);

        Assert.Equal("OpenHands", adapter.Key);
        Assert.Equal(ExecutionCapabilities.FactoryRoutedModel, adapter.Capabilities);
    }

    [Fact]
    public async Task ExecuteAsync_DeadlineAlreadyExceeded_ReturnsTimedOutWithoutLaunching()
    {
        var runner = new FakeProcessRunner();
        var lifecycle = new FakeLifecycleManager();
        var clientFactory = new FakeClientFactory();

        var adapter = new OpenHandsExecutionAdapter(
            new OpenHandsOptions { BaseUrl = "http://127.0.0.1:48080" },
            runner,
            lifecycle,
            new FailureClassifier(),
            NullLogger<OpenHandsExecutionAdapter>.Instance,
            clientFactory: clientFactory);

        var spec = CreateSpec(deadline: DateTimeOffset.UtcNow.AddSeconds(-5));

        var outcome = await adapter.ExecuteAsync(spec);

        Assert.Equal(ExecutionOutcomeStatus.TimedOut, outcome.Status);
        Assert.Equal(OpenHandsExecutionAdapter.ExitReasons.Timeout, outcome.ExitReason);
        Assert.Empty(runner.Requests);
        Assert.Null(clientFactory.Client.LastCreateRequest);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulRun_ReturnsSucceededWithOutputAndUsage()
    {
        var runner = new FakeProcessRunner();
        var lifecycle = new FakeLifecycleManager();
        var clientFactory = new FakeClientFactory();

        clientFactory.Client.ExecutionStatuses.Enqueue("running");
        clientFactory.Client.ExecutionStatuses.Enqueue("finished");
        clientFactory.Client.FinalResponse = "File successfully written!";
        clientFactory.Client.Stats = new OpenHandsStatsDto
        {
            UsageToMetrics = new Dictionary<string, OpenHandsUsageMetricDto>
            {
                ["default"] = new()
                {
                    AccumulatedTokenUsage = new OpenHandsTokenUsageDto
                    {
                        PromptTokens = 120,
                        CompletionTokens = 45
                    }
                }
            }
        };

        var adapter = new OpenHandsExecutionAdapter(
            new OpenHandsOptions { BaseUrl = "http://127.0.0.1:48080", PollIntervalSeconds = 0.01 },
            runner,
            lifecycle,
            new FailureClassifier(),
            NullLogger<OpenHandsExecutionAdapter>.Instance,
            clientFactory: clientFactory,
            secretProvider: new FakeSecretProvider(),
            gatewayResolver: Resolver());

        var spec = CreateSpec();
        var outcome = await adapter.ExecuteAsync(spec);

        Assert.Equal(ExecutionOutcomeStatus.Succeeded, outcome.Status);
        Assert.Equal("File successfully written!", outcome.Output);
        Assert.Equal(OpenHandsExecutionAdapter.ExitReasons.Completed, outcome.ExitReason);
        Assert.NotNull(outcome.Usage);
        Assert.Equal(120, outcome.Usage.PromptTokens);
        Assert.Equal(45, outcome.Usage.CompletionTokens);
        Assert.Equal(165, outcome.Usage.TotalTokens);
    }

    [Fact]
    public async Task ExecuteAsync_ErrorStatus_ReturnsFailed()
    {
        var runner = new FakeProcessRunner();
        var lifecycle = new FakeLifecycleManager();
        var clientFactory = new FakeClientFactory();

        clientFactory.Client.ExecutionStatuses.Enqueue("running");
        clientFactory.Client.ExecutionStatuses.Enqueue("error");

        var adapter = new OpenHandsExecutionAdapter(
            new OpenHandsOptions { BaseUrl = "http://127.0.0.1:48080", PollIntervalSeconds = 0.01 },
            runner,
            lifecycle,
            new FailureClassifier(),
            NullLogger<OpenHandsExecutionAdapter>.Instance,
            clientFactory: clientFactory,
            secretProvider: new FakeSecretProvider(),
            gatewayResolver: Resolver());

        var spec = CreateSpec();
        var outcome = await adapter.ExecuteAsync(spec);

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(OpenHandsExecutionAdapter.ExitReasons.Error, outcome.ExitReason);
        Assert.NotNull(outcome.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_CallsInterruptAndReturnsCancelled()
    {
        var runner = new FakeProcessRunner();
        var lifecycle = new FakeLifecycleManager();
        var clientFactory = new FakeClientFactory();

        clientFactory.Client.ExecutionStatuses.Enqueue("running");
        clientFactory.Client.ExecutionStatuses.Enqueue("running");

        using var cts = new CancellationTokenSource();

        var adapter = new OpenHandsExecutionAdapter(
            new OpenHandsOptions { BaseUrl = "http://127.0.0.1:48080", PollIntervalSeconds = 0.01 },
            runner,
            lifecycle,
            new FailureClassifier(),
            NullLogger<OpenHandsExecutionAdapter>.Instance,
            clientFactory: clientFactory,
            secretProvider: new FakeSecretProvider(),
            gatewayResolver: Resolver());

        var spec = CreateSpec();

        // Cancel shortly after starting
        cts.CancelAfter(50);

        var outcome = await adapter.ExecuteAsync(spec, cts.Token);

        Assert.Equal(ExecutionOutcomeStatus.Cancelled, outcome.Status);
        Assert.Equal(OpenHandsExecutionAdapter.ExitReasons.Cancelled, outcome.ExitReason);
        Assert.True(clientFactory.Client.Interrupted);
    }

    [Fact]
    public async Task ExecuteAsync_ContainerMode_CleansUpContainerInFinally()
    {
        var runner = new FakeProcessRunner();
        var lifecycle = new FakeLifecycleManager();
        var clientFactory = new FakeClientFactory();

        clientFactory.Client.ExecutionStatuses.Enqueue("running");
        clientFactory.Client.ExecutionStatuses.Enqueue("finished");

        var adapter = new OpenHandsExecutionAdapter(
            new OpenHandsOptions
            {
                BaseUrl = null, // Trigger container launch
                PollIntervalSeconds = 0.01
            },
            runner,
            lifecycle,
            new FailureClassifier(),
            NullLogger<OpenHandsExecutionAdapter>.Instance,
            clientFactory: clientFactory,
            secretProvider: new FakeSecretProvider(),
            gatewayResolver: Resolver());

        var spec = CreateSpec();
        var outcome = await adapter.ExecuteAsync(spec);

        Assert.Equal(ExecutionOutcomeStatus.Succeeded, outcome.Status);
        Assert.Contains(spec.AgentRunId, lifecycle.CleanedRuns);
    }

    // ---------------------------------------------------------------- Phase L — runtime state

    [Fact]
    public async Task ContainerMode_RelocatesAgentServerState_OutOfTheWorkspaceMount_WithNoNewMount()
    {
        var runner = new FakeProcessRunner();
        var clientFactory = new FakeClientFactory();
        clientFactory.Client.ExecutionStatuses.Enqueue("running");
        clientFactory.Client.ExecutionStatuses.Enqueue("finished");

        await ContainerAdapter(runner, new FakeLifecycleManager(), clientFactory).ExecuteAsync(CreateSpec());

        var args = runner.Requests.Single(r => r.Arguments.FirstOrDefault() == "run").Arguments.ToList();
        var environment = ValuesOf(args, "-e");
        Assert.Contains($"OH_CONVERSATIONS_PATH={OpenHandsExecutionAdapter.RuntimeStateRoot}/conversations", environment);
        Assert.Contains($"OH_BASH_EVENTS_DIR={OpenHandsExecutionAdapter.RuntimeStateRoot}/bash_events", environment);
        Assert.False(OpenHandsExecutionAdapter.RuntimeStateRoot.StartsWith("/workspace", StringComparison.Ordinal));

        // The workspace stays the only host path the container sees: no state volume, no new exposure.
        var mount = Assert.Single(ValuesOf(args, "-v"));
        Assert.EndsWith(":/workspace", mount);
        Assert.DoesNotContain("--mount", args);
    }

    public static TheoryData<string> TerminalPaths => new() { "failed", "cancelled", "timedout", "startup-failure" };

    [Theory]
    [MemberData(nameof(TerminalPaths))]
    public async Task ContainerMode_EveryTerminalPath_RemovesThisRunsContainer_AndWithItTheRuntimeState(string path)
    {
        var runner = new FakeProcessRunner { LaunchExitCode = path == "startup-failure" ? 125 : 0 };
        var lifecycle = new FakeLifecycleManager();
        var clientFactory = new FakeClientFactory();
        for (var i = 0; i < 50; i++)
        {
            clientFactory.Client.ExecutionStatuses.Enqueue(path == "failed" && i == 1 ? "error" : "running");
        }

        using var cts = new CancellationTokenSource();
        if (path == "cancelled")
        {
            cts.CancelAfter(50);
        }

        // The adapter is built first and the deadline is generous, so the run is always launched before
        // it expires (an already-expired deadline returns TimedOut without spawning anything).
        var adapter = ContainerAdapter(runner, lifecycle, clientFactory);
        var spec = CreateSpec(deadline: path == "timedout" ? DateTimeOffset.UtcNow.AddSeconds(2.5) : null);
        var outcome = await adapter.ExecuteAsync(spec, cts.Token);

        var expected = path switch
        {
            "cancelled" => ExecutionOutcomeStatus.Cancelled,
            "timedout" => ExecutionOutcomeStatus.TimedOut,
            _ => ExecutionOutcomeStatus.Failed
        };
        Assert.Equal(expected, outcome.Status);
        Assert.Equal(new[] { spec.AgentRunId }, lifecycle.CleanedRuns);
    }

    /// <summary>
    /// Phase O — the cancel-before-container-creation race at the OpenHands boundary: a launch that was cancelled, or that timed
    /// out inside `docker run -d`, may still be completing in the Docker daemon, so cleanup must be the bounded RECONCILIATION.
    /// A launch Docker answered definitively (it ran, or it refused) has nothing in flight and keeps the single-shot removal.
    /// </summary>
    [Theory]
    [InlineData("cancelled-during-launch", true)]
    [InlineData("launch-client-timed-out", true)]
    [InlineData("launch-refused", false)]
    [InlineData("launched-then-cancelled-while-running", false)]
    public async Task ContainerMode_CleanupIsReconciled_OnlyWhenTheLaunchWasInterrupted(string path, bool expectReconcile)
    {
        var runner = new FakeProcessRunner
        {
            LaunchBlocksUntilCancelled = path == "cancelled-during-launch",
            LaunchTimesOut = path == "launch-client-timed-out",
            LaunchExitCode = path == "launch-refused" ? 125 : 0
        };
        var lifecycle = new FakeLifecycleManager();
        var clientFactory = new FakeClientFactory();
        for (var i = 0; i < 50; i++)
        {
            clientFactory.Client.ExecutionStatuses.Enqueue("running");
        }

        using var cts = new CancellationTokenSource();
        if (path is "cancelled-during-launch" or "launched-then-cancelled-while-running")
        {
            cts.CancelAfter(path == "cancelled-during-launch" ? 50 : 150);
        }

        var spec = CreateSpec();
        var outcome = await ContainerAdapter(runner, lifecycle, clientFactory).ExecuteAsync(spec, cts.Token);

        Assert.NotEqual(ExecutionOutcomeStatus.Succeeded, outcome.Status);
        if (path.StartsWith("cancelled", StringComparison.Ordinal) || path.EndsWith("cancelled-while-running", StringComparison.Ordinal))
        {
            Assert.Equal(ExecutionOutcomeStatus.Cancelled, outcome.Status);
        }

        Assert.Equal(new[] { spec.AgentRunId }, lifecycle.CleanedRuns);
        Assert.Equal(expectReconcile ? new[] { spec.AgentRunId } : Array.Empty<Guid>(), lifecycle.ReconciledRuns);
    }

    private static OpenHandsExecutionAdapter ContainerAdapter(
        FakeProcessRunner runner, FakeLifecycleManager lifecycle, FakeClientFactory clientFactory) => new(
        new OpenHandsOptions { BaseUrl = null, PollIntervalSeconds = 0.01 },
        runner,
        lifecycle,
        new FailureClassifier(),
        NullLogger<OpenHandsExecutionAdapter>.Instance,
        clientFactory: clientFactory,
        secretProvider: new FakeSecretProvider(),
        gatewayResolver: Resolver());

    private static List<string> ValuesOf(List<string> args, string flag) =>
        args.Select((arg, i) => (arg, i)).Where(x => x.arg == flag).Select(x => args[x.i + 1]).ToList();
}
