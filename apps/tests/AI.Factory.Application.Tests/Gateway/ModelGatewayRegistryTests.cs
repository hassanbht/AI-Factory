using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Gateway;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

public sealed class ModelGatewayRegistryTests
{
    [Fact]
    public void Register_And_TryGet_ResolvesGatewayCaseInsensitively()
    {
        var registry = new ModelGatewayRegistry();
        var gateway = new FakeModelGateway("OpenRouter");

        registry.Register(gateway);

        Assert.Contains("OpenRouter", registry.Keys);
        Assert.True(registry.TryGet("OpenRouter", out var resolvedExact));
        Assert.Same(gateway, resolvedExact);

        Assert.True(registry.TryGet("openrouter", out var resolvedLower));
        Assert.Same(gateway, resolvedLower);

        Assert.True(registry.TryGet("OPENROUTER", out var resolvedUpper));
        Assert.Same(gateway, resolvedUpper);
    }

    [Fact]
    public void Register_DuplicateKey_ThrowsDuplicateModelGatewayException()
    {
        var registry = new ModelGatewayRegistry();
        var g1 = new FakeModelGateway("OpenRouter");
        var g2 = new FakeModelGateway("openrouter");

        registry.Register(g1);

        var ex = Assert.Throws<DuplicateModelGatewayException>(() => registry.Register(g2));
        Assert.Equal("openrouter", ex.GatewayKey);
    }

    [Fact]
    public void GetRequired_UnknownKey_ThrowsModelGatewayNotFoundException()
    {
        var registry = new ModelGatewayRegistry();

        var ex = Assert.Throws<ModelGatewayNotFoundException>(() => registry.GetRequired("NonExistentGateway"));
        Assert.Equal("NonExistentGateway", ex.GatewayKey);
    }

    [Fact]
    public void TryGet_UnknownKey_ReturnsFalseAndNull()
    {
        var registry = new ModelGatewayRegistry();

        var found = registry.TryGet("Unknown", out var gateway);

        Assert.False(found);
        Assert.Null(gateway);
    }

    [Fact]
    public void Constructor_WithIEnumerable_RegistersAllDistinctGateways()
    {
        var g1 = new FakeModelGateway("OpenRouter");
        var g2 = new FakeModelGateway("OmniRoute");

        var registry = new ModelGatewayRegistry(new[] { g1, g2 });

        Assert.Equal(2, registry.Keys.Count);
        Assert.True(registry.TryGet("OpenRouter", out _));
        Assert.True(registry.TryGet("OmniRoute", out _));
    }
}
