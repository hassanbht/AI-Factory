using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Resilience;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ResilienceModelProviderDecoratorTests
{
    private sealed class FakeInnerProvider : IModelProvider
    {
        public string ProviderId => "fake";
        public IReadOnlyCollection<string> SupportedCapabilities { get; } = new List<string> { "CodeGeneration" };
        public int CallCount { get; private set; }
        private readonly Func<int, AiCompletionResult> _handler;

        public FakeInnerProvider(Func<int, AiCompletionResult> handler) => _handler = handler;

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
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
            Guid executionId, AuditEventType eventType, AuditSeverity severity, string message,
            Guid? projectId = null, Guid? workItemId = null, IReadOnlyDictionary<string, string>? metadata = null,
            Guid? correlationId = null, Guid? operationId = null, Guid? runId = null, string? outcome = null,
            string? actorType = null, string? actorId = null, long? durationMs = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private readonly OperationContextAccessor _contextAccessor = new();
    private readonly OperationalMetrics _metrics = new();
    private readonly IFailureClassifier _failureClassifier = new FailureClassifier();
    private readonly IOperationScopeFactory _scopeFactory;

    public ResilienceModelProviderDecoratorTests()
    {
        _scopeFactory = new OperationScopeFactory(_contextAccessor, _metrics, NullLogger<OperationScopeFactory>.Instance);
    }

    private ResilienceExecutor CreateResilienceExecutor()
    {
        var options = new ResilienceOptions();
        options.Profiles[ResilienceOptions.ProviderCall] = new ResilienceProfile
        {
            Name = ResilienceOptions.ProviderCall,
            Enabled = true,
            MaxRetryAttempts = 2,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(5),
            UseJitter = false
        };

        return new ResilienceExecutor(
            options, _failureClassifier, _scopeFactory, _contextAccessor, new NoOpAuditLogger(), _metrics,
            NullLogger<ResilienceExecutor>.Instance, new ExponentialBackoffCalculator(() => 0.5), new ImmediateDelayProvider());
    }

    private static AiCompletionResult Ok(int attempt) =>
        new(true, "content", "fake", "model-1", Guid.NewGuid(), 10);

    private static AiCompletionResult Fail(AiFailureCategory category) =>
        new(false, null, "fake", "model-1", Guid.NewGuid(), 10, FailureCategory: category, ErrorMessage: category.ToString());

    [Fact]
    public async Task CompleteAsync_WhenInnerSucceeds_ReturnsResultWithoutRetries()
    {
        var inner = new FakeInnerProvider(_ => Ok(1));
        var decorator = new ResilienceModelProviderDecorator(inner, CreateResilienceExecutor(), NullLogger<ResilienceModelProviderDecorator>.Instance);

        var result = await decorator.CompleteAsync(new AiCompletionRequest(Guid.NewGuid(), AiCapability.CodeGeneration, null, "hi"));

        Assert.True(result.Success);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_WhenAuthenticationFailure_DoesNotRetry()
    {
        var inner = new FakeInnerProvider(_ => Fail(AiFailureCategory.Authentication));
        var decorator = new ResilienceModelProviderDecorator(inner, CreateResilienceExecutor(), NullLogger<ResilienceModelProviderDecorator>.Instance);

        var result = await decorator.CompleteAsync(new AiCompletionRequest(Guid.NewGuid(), AiCapability.CodeGeneration, null, "hi"));

        Assert.False(result.Success);
        Assert.Equal(AiFailureCategory.Authentication, result.FailureCategory);
        Assert.Equal(1, inner.CallCount); // Did NOT retry!
    }

    [Fact]
    public async Task CompleteAsync_WhenRateLimitedThenSucceeds_RetriesAndReturnsSuccess()
    {
        var inner = new FakeInnerProvider(attempt => attempt == 1 ? Fail(AiFailureCategory.RateLimited) : Ok(attempt));
        var decorator = new ResilienceModelProviderDecorator(inner, CreateResilienceExecutor(), NullLogger<ResilienceModelProviderDecorator>.Instance);

        var result = await decorator.CompleteAsync(new AiCompletionRequest(Guid.NewGuid(), AiCapability.CodeGeneration, null, "hi"));

        Assert.True(result.Success);
        Assert.Equal(2, inner.CallCount); // Retried once!
    }

    [Fact]
    public async Task CompleteAsync_WhenRetriesExhausted_ReturnsNormalizedFailureWithOriginalCategory()
    {
        var inner = new FakeInnerProvider(_ => Fail(AiFailureCategory.ProviderFailure));
        var decorator = new ResilienceModelProviderDecorator(inner, CreateResilienceExecutor(), NullLogger<ResilienceModelProviderDecorator>.Instance);

        var result = await decorator.CompleteAsync(new AiCompletionRequest(Guid.NewGuid(), AiCapability.CodeGeneration, null, "hi"));

        Assert.False(result.Success);
        Assert.Equal(AiFailureCategory.ProviderFailure, result.FailureCategory);
        Assert.Equal(3, inner.CallCount); // Initial attempt + 2 retries, then exhausted
    }
}
