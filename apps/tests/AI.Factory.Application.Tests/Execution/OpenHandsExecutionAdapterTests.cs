using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.OpenHands;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

public sealed class OpenHandsExecutionAdapterTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = new();

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty, false));
        }
    }

    private sealed class FakeLifecycleManager : IContainerLifecycleManager
    {
        public List<Guid> CleanedRuns { get; } = new();

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

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<OpenHandsConversationInfo> CreateConversationAsync(
            OpenHandsCreateConversationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
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
            secretProvider: new FakeSecretProvider());

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
            secretProvider: new FakeSecretProvider());

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
            secretProvider: new FakeSecretProvider());

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
            secretProvider: new FakeSecretProvider());

        var spec = CreateSpec();
        var outcome = await adapter.ExecuteAsync(spec);

        Assert.Equal(ExecutionOutcomeStatus.Succeeded, outcome.Status);
        Assert.Contains(spec.AgentRunId, lifecycle.CleanedRuns);
    }
}
