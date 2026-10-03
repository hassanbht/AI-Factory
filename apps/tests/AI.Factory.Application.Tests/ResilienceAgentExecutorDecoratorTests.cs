using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Resilience;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ResilienceAgentExecutorDecoratorTests
{
    private sealed class FakeInnerAgentExecutor : IAgentExecutor
    {
        public int CallCount { get; private set; }
        private readonly Func<int, AgentExecutionResult> _handler;

        public FakeInnerAgentExecutor(Func<int, AgentExecutionResult> handler)
        {
            _handler = handler;
        }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_handler(CallCount));
        }
    }

    private sealed class ImmediateDelayProvider : IDelayProvider
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpAuditLogger : IAuditLogger
    {
        public Task LogAsync(
            Guid executionId,
            AuditEventType eventType,
            AuditSeverity severity,
            string message,
            Guid? projectId = null,
            Guid? workItemId = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            Guid? correlationId = null,
            Guid? operationId = null,
            Guid? runId = null,
            string? outcome = null,
            string? actorType = null,
            string? actorId = null,
            long? durationMs = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private readonly OperationContextAccessor _contextAccessor = new();
    private readonly OperationalMetrics _metrics = new();
    private readonly IFailureClassifier _failureClassifier = new FailureClassifier();
    private readonly IOperationScopeFactory _scopeFactory;

    public ResilienceAgentExecutorDecoratorTests()
    {
        _scopeFactory = new OperationScopeFactory(
            _contextAccessor,
            _metrics,
            NullLogger<OperationScopeFactory>.Instance);
    }

    private ResilienceExecutor CreateResilienceExecutor()
    {
        var options = new ResilienceOptions();
        options.Profiles[ResilienceOptions.AgentExecution] = new ResilienceProfile
        {
            Name = ResilienceOptions.AgentExecution,
            Enabled = true,
            MaxRetryAttempts = 2,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(5),
            UseJitter = false
        };

        return new ResilienceExecutor(
            options,
            _failureClassifier,
            _scopeFactory,
            _contextAccessor,
            new NoOpAuditLogger(),
            _metrics,
            NullLogger<ResilienceExecutor>.Instance,
            new ExponentialBackoffCalculator(() => 0.5),
            new ImmediateDelayProvider());
    }

    [Fact]
    public async Task ExecuteAsync_WhenInnerSucceeds_ReturnsResultWithoutRetries()
    {
        var inner = new FakeInnerAgentExecutor(_ => new AgentExecutionResult(true, "All good", null, 100));
        var decorator = new ResilienceAgentExecutorDecorator(
            inner,
            CreateResilienceExecutor(),
            _failureClassifier,
            NullLogger<ResilienceAgentExecutorDecorator>.Instance);

        var request = new AgentExecutionRequest("coder", "model-1", "write code", ".");
        var result = await decorator.ExecuteAsync(request);

        Assert.True(result.Success);
        Assert.Equal("All good", result.Output);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDeterministicFailure_DoesNotRetry()
    {
        // Deterministic compiler or validation error in agent output
        var inner = new FakeInnerAgentExecutor(_ => new AgentExecutionResult(false, null, "CS0103: The name 'foo' does not exist", 100));
        var decorator = new ResilienceAgentExecutorDecorator(
            inner,
            CreateResilienceExecutor(),
            _failureClassifier,
            NullLogger<ResilienceAgentExecutorDecorator>.Instance);

        var request = new AgentExecutionRequest("coder", "model-1", "write code", ".");
        var result = await decorator.ExecuteAsync(request);

        Assert.False(result.Success);
        Assert.Contains("CS0103", result.Error);
        Assert.Equal(1, inner.CallCount); // Did NOT retry!
    }

    [Fact]
    public async Task ExecuteAsync_WhenTransientFailureThenSucceeds_RetriesAndReturnsSuccess()
    {
        // Attempt 1: transient network/host error -> retries
        // Attempt 2: succeeds
        var inner = new FakeInnerAgentExecutor(attempt => attempt == 1
            ? new AgentExecutionResult(false, null, "Temporary network timeout connecting to docker daemon", 50)
            : new AgentExecutionResult(true, "Generated code", null, 120));

        var decorator = new ResilienceAgentExecutorDecorator(
            inner,
            CreateResilienceExecutor(),
            _failureClassifier,
            NullLogger<ResilienceAgentExecutorDecorator>.Instance);

        var request = new AgentExecutionRequest("coder", "model-1", "write code", ".");
        var result = await decorator.ExecuteAsync(request);

        Assert.True(result.Success);
        Assert.Equal("Generated code", result.Output);
        Assert.Equal(2, inner.CallCount); // Retried once!
    }

    [Theory]
    [InlineData("Billing or credits exhausted: HTTP 402: Insufficient credits")]
    [InlineData("HTTP 401: No active credentials for provider: openai.")]
    [InlineData("HTTP 403: forbidden")]
    // Contains a transient launch marker — still a gateway answer, not a launch failure.
    [InlineData("API failed after 3 retries — HTTP 503: connection refused by upstream")]
    public async Task ExecuteAsync_WhenRuntimeReportsGatewayFailureWithExitCodeZero_DoesNotRetry(string gatewayError)
    {
        // Phase J — a Hermes run that exited 0 but reported failure (gateway rejected the call).
        var inner = new FakeInnerAgentExecutor(_ => new AgentExecutionResult(
            false, gatewayError, $"Hermes reported a failed run (exit code 0): {gatewayError}", 40, ExitCode: 0));
        var decorator = new ResilienceAgentExecutorDecorator(
            inner,
            CreateResilienceExecutor(),
            _failureClassifier,
            NullLogger<ResilienceAgentExecutorDecorator>.Instance);

        var result = await decorator.ExecuteAsync(new AgentExecutionRequest("coder", "model-1", "write code", "."));

        Assert.False(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, inner.CallCount); // One explicit gateway operation, no hidden retry.
    }
}
