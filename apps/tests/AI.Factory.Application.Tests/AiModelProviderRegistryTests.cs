using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class AiModelProviderRegistryTests
{
    private sealed class StubProvider : IModelProvider
    {
        public string ProviderId { get; }
        public IReadOnlyCollection<string> SupportedCapabilities { get; } = new List<string> { "GeneralChat" };

        public StubProvider(string providerId) => ProviderId = providerId;

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    [Fact]
    public void RegisterProvider_ThenTryGetProvider_ReturnsRegisteredInstance()
    {
        var registry = new AiModelProviderRegistry();
        var provider = new StubProvider("openrouter");

        registry.RegisterProvider(provider);

        Assert.True(registry.TryGetProvider("openrouter", out var resolved));
        Assert.Same(provider, resolved);
        Assert.Contains("openrouter", registry.RegisteredProviderIds);
    }

    [Fact]
    public void RegisterProvider_Throws_WhenProviderIdAlreadyRegistered()
    {
        var registry = new AiModelProviderRegistry();
        registry.RegisterProvider(new StubProvider("openrouter"));

        Assert.Throws<DuplicateProviderRegistrationException>(
            () => registry.RegisterProvider(new StubProvider("openrouter")));
    }

    [Fact]
    public void RegisterModel_ThenTryResolveModel_ReturnsOwningProvider()
    {
        var registry = new AiModelProviderRegistry();
        var provider = new StubProvider("openrouter");
        registry.RegisterProvider(provider);

        registry.RegisterModel("coding-optimized-model-a", "openrouter");

        Assert.True(registry.TryResolveModel("coding-optimized-model-a", out var resolved));
        Assert.Same(provider, resolved);
        Assert.Contains("coding-optimized-model-a", registry.RegisteredModelIds);
    }

    [Fact]
    public void RegisterModel_Throws_WhenModelIdAlreadyMapped()
    {
        var registry = new AiModelProviderRegistry();
        registry.RegisterProvider(new StubProvider("openrouter"));
        registry.RegisterModel("model-a", "openrouter");

        Assert.Throws<DuplicateModelRegistrationException>(
            () => registry.RegisterModel("model-a", "openrouter"));
    }

    [Fact]
    public void RegisterModel_Throws_WhenProviderNotRegistered()
    {
        var registry = new AiModelProviderRegistry();

        Assert.Throws<InvalidOperationException>(
            () => registry.RegisterModel("model-a", "unregistered-provider"));
    }

    [Fact]
    public void TryResolveModel_ReturnsFalse_ForUnknownModelId()
    {
        var registry = new AiModelProviderRegistry();

        Assert.False(registry.TryResolveModel("unknown-model", out var provider));
        Assert.Null(provider);
    }
}
