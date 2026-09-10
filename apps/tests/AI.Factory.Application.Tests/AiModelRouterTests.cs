using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class AiModelRouterTests
{
    private sealed class StubProvider : IModelProvider
    {
        public string ProviderId { get; }
        public IReadOnlyCollection<string> SupportedCapabilities { get; }
        public int CallCount { get; private set; }
        private readonly Func<AiCompletionRequest, AiCompletionResult> _respond;

        public StubProvider(string providerId, Func<AiCompletionRequest, AiCompletionResult> respond)
        {
            ProviderId = providerId;
            SupportedCapabilities = new List<string> { "CodeGeneration" };
            _respond = respond;
        }

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }
    }

    private static AiCompletionResult Success(AiCompletionRequest r, string providerId) =>
        new(true, "ok", providerId, r.PreferredModelId ?? "?", r.CorrelationId, 5);

    private static AiCompletionResult Failure(AiCompletionRequest r, string providerId, AiFailureCategory category) =>
        new(false, null, providerId, r.PreferredModelId ?? "?", r.CorrelationId, 5, FailureCategory: category, ErrorMessage: category.ToString());

    private static ModelRoutingOptions ModelsWith(params ModelCapability[] models) =>
        new() { Models = models.ToList() };

    private static ModelCapability Model(string id, string provider, bool available = true, params string[] capabilities) =>
        new() { ModelId = id, Provider = provider, IsAvailable = available, Capabilities = capabilities.ToList() };

    private AiModelRouter BuildRouter(
        ModelRoutingOptions modelRoutingOptions,
        AiProviderOptions? aiProviderOptions,
        AiModelProviderRegistry registry,
        AiRoutingOptions? aiRoutingOptions = null)
    {
        return new AiModelRouter(
            modelRoutingOptions,
            aiProviderOptions ?? new AiProviderOptions(),
            aiRoutingOptions ?? new AiRoutingOptions(),
            registry,
            new OperationalMetrics(),
            NullLogger<AiModelRouter>.Instance);
    }

    private static AiCompletionRequest Request(AiCapability capability, string? preferredModelId = null) =>
        new(Guid.NewGuid(), capability, null, "write code", PreferredModelId: preferredModelId);

    [Fact]
    public async Task ExecuteAsync_UsesExplicitPreferredModel_WhenCapabilityCompatible()
    {
        var models = ModelsWith(
            Model("model-a", "prov-a", true, "CodeGeneration"),
            Model("model-b", "prov-b", true, "CodeGeneration"));

        var registry = new AiModelProviderRegistry();
        var providerA = new StubProvider("prov-a", r => Success(r, "prov-a"));
        var providerB = new StubProvider("prov-b", r => Success(r, "prov-b"));
        registry.RegisterProvider(providerA);
        registry.RegisterProvider(providerB);
        registry.RegisterModel("model-a", "prov-a");
        registry.RegisterModel("model-b", "prov-b");

        var router = BuildRouter(models, null, registry);

        var result = await router.ExecuteAsync(Request(AiCapability.CodeGeneration, preferredModelId: "model-b"));

        Assert.True(result.Success);
        Assert.Equal("model-b", result.ModelId);
        Assert.Equal(0, providerA.CallCount);
        Assert.Equal(1, providerB.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsUnavailable_WhenNoModelSupportsCapability()
    {
        var models = ModelsWith(Model("model-a", "prov-a", true, "Summarization"));
        var registry = new AiModelProviderRegistry();
        registry.RegisterProvider(new StubProvider("prov-a", r => Success(r, "prov-a")));
        registry.RegisterModel("model-a", "prov-a");

        var router = BuildRouter(models, null, registry);

        var result = await router.ExecuteAsync(Request(AiCapability.CodeGeneration));

        Assert.False(result.Success);
        Assert.Equal(AiFailureCategory.Unavailable, result.FailureCategory);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsUnavailableModel()
    {
        var models = ModelsWith(
            Model("model-a", "prov-a", available: false, "CodeGeneration"),
            Model("model-b", "prov-b", true, "CodeGeneration"));

        var registry = new AiModelProviderRegistry();
        registry.RegisterProvider(new StubProvider("prov-a", r => Success(r, "prov-a")));
        var providerB = new StubProvider("prov-b", r => Success(r, "prov-b"));
        registry.RegisterProvider(providerB);
        registry.RegisterModel("model-a", "prov-a");
        registry.RegisterModel("model-b", "prov-b");

        var router = BuildRouter(models, null, registry);

        var result = await router.ExecuteAsync(Request(AiCapability.CodeGeneration));

        Assert.True(result.Success);
        Assert.Equal("model-b", result.ModelId);
    }

    [Fact]
    public async Task ExecuteAsync_PicksHighestPriorityProvider_WhenNoExplicitPreference()
    {
        var models = ModelsWith(
            Model("model-low", "prov-low", true, "CodeGeneration"),
            Model("model-high", "prov-high", true, "CodeGeneration"));

        var aiProviderOptions = new AiProviderOptions
        {
            Providers =
            {
                ["prov-low"] = new ProviderCredentialSettings { Priority = 1 },
                ["prov-high"] = new ProviderCredentialSettings { Priority = 100 }
            }
        };

        var registry = new AiModelProviderRegistry();
        var providerLow = new StubProvider("prov-low", r => Success(r, "prov-low"));
        var providerHigh = new StubProvider("prov-high", r => Success(r, "prov-high"));
        registry.RegisterProvider(providerLow);
        registry.RegisterProvider(providerHigh);
        registry.RegisterModel("model-low", "prov-low");
        registry.RegisterModel("model-high", "prov-high");

        var router = BuildRouter(models, aiProviderOptions, registry);

        var result = await router.ExecuteAsync(Request(AiCapability.CodeGeneration));

        Assert.Equal("model-high", result.ModelId);
        Assert.Equal(0, providerLow.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackDeterministically_OnEligibleTransientFailure()
    {
        var models = ModelsWith(
            Model("model-a", "prov-a", true, "CodeGeneration"),
            Model("model-b", "prov-b", true, "CodeGeneration"));

        var aiProviderOptions = new AiProviderOptions
        {
            Providers =
            {
                ["prov-a"] = new ProviderCredentialSettings { Priority = 100 },
                ["prov-b"] = new ProviderCredentialSettings { Priority = 1 }
            }
        };

        var registry = new AiModelProviderRegistry();
        var providerA = new StubProvider("prov-a", r => Failure(r, "prov-a", AiFailureCategory.Unavailable));
        var providerB = new StubProvider("prov-b", r => Success(r, "prov-b"));
        registry.RegisterProvider(providerA);
        registry.RegisterProvider(providerB);
        registry.RegisterModel("model-a", "prov-a");
        registry.RegisterModel("model-b", "prov-b");

        var router = BuildRouter(models, aiProviderOptions, registry);

        var result = await router.ExecuteAsync(Request(AiCapability.CodeGeneration));

        Assert.True(result.Success);
        Assert.Equal("model-b", result.ModelId);
        Assert.Equal(1, providerA.CallCount);
        Assert.Equal(1, providerB.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotFallback_OnInvalidRequestFailure()
    {
        var models = ModelsWith(
            Model("model-a", "prov-a", true, "CodeGeneration"),
            Model("model-b", "prov-b", true, "CodeGeneration"));

        var aiProviderOptions = new AiProviderOptions
        {
            Providers =
            {
                ["prov-a"] = new ProviderCredentialSettings { Priority = 100 },
                ["prov-b"] = new ProviderCredentialSettings { Priority = 1 }
            }
        };

        var registry = new AiModelProviderRegistry();
        var providerA = new StubProvider("prov-a", r => Failure(r, "prov-a", AiFailureCategory.InvalidRequest));
        var providerB = new StubProvider("prov-b", r => Success(r, "prov-b"));
        registry.RegisterProvider(providerA);
        registry.RegisterProvider(providerB);
        registry.RegisterModel("model-a", "prov-a");
        registry.RegisterModel("model-b", "prov-b");

        var router = BuildRouter(models, aiProviderOptions, registry);

        var result = await router.ExecuteAsync(Request(AiCapability.CodeGeneration));

        Assert.False(result.Success);
        Assert.Equal(AiFailureCategory.InvalidRequest, result.FailureCategory);
        Assert.Equal(0, providerB.CallCount); // Did NOT fall back!
    }

    [Fact]
    public async Task ExecuteAsync_BoundsFallbackChain_ByMaxFallbackAttempts()
    {
        var models = ModelsWith(
            Model("model-a", "prov-a", true, "CodeGeneration"),
            Model("model-b", "prov-b", true, "CodeGeneration"),
            Model("model-c", "prov-c", true, "CodeGeneration"));

        var aiProviderOptions = new AiProviderOptions
        {
            Providers =
            {
                ["prov-a"] = new ProviderCredentialSettings { Priority = 100 },
                ["prov-b"] = new ProviderCredentialSettings { Priority = 50 },
                ["prov-c"] = new ProviderCredentialSettings { Priority = 1 }
            }
        };

        var registry = new AiModelProviderRegistry();
        var providerA = new StubProvider("prov-a", r => Failure(r, "prov-a", AiFailureCategory.Timeout));
        var providerB = new StubProvider("prov-b", r => Failure(r, "prov-b", AiFailureCategory.Timeout));
        var providerC = new StubProvider("prov-c", r => Success(r, "prov-c"));
        registry.RegisterProvider(providerA);
        registry.RegisterProvider(providerB);
        registry.RegisterProvider(providerC);
        registry.RegisterModel("model-a", "prov-a");
        registry.RegisterModel("model-b", "prov-b");
        registry.RegisterModel("model-c", "prov-c");

        var router = BuildRouter(models, aiProviderOptions, registry, new AiRoutingOptions { MaxFallbackAttempts = 2 });

        var result = await router.ExecuteAsync(Request(AiCapability.CodeGeneration));

        Assert.False(result.Success); // Exhausted the bounded 2-attempt chain before reaching prov-c
        Assert.Equal(1, providerA.CallCount);
        Assert.Equal(1, providerB.CallCount);
        Assert.Equal(0, providerC.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCancelled_WhenCancellationRequested()
    {
        var models = ModelsWith(Model("model-a", "prov-a", true, "CodeGeneration"));
        var registry = new AiModelProviderRegistry();
        registry.RegisterProvider(new StubProvider("prov-a", r => Success(r, "prov-a")));
        registry.RegisterModel("model-a", "prov-a");

        var router = BuildRouter(models, null, registry);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await router.ExecuteAsync(Request(AiCapability.CodeGeneration), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(AiFailureCategory.Cancelled, result.FailureCategory);
    }
}
