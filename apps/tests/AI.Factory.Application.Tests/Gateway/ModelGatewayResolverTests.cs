using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

public sealed class ModelGatewayResolverTests
{
    private static (ModelGatewayResolver Resolver, FakeModelGateway OpenRouter, FakeModelGateway OmniRoute) CreateResolver(
        string defaultGateway = "OpenRouter",
        bool openRouterEnabled = true,
        bool omniRouteEnabled = true)
    {
        var openRouter = new FakeModelGateway("OpenRouter");
        var omniRoute = new FakeModelGateway("OmniRoute");

        var registry = new ModelGatewayRegistry(new[] { openRouter, omniRoute });

        var options = new ModelGatewayOptions
        {
            DefaultGateway = defaultGateway,
            Gateways = new Dictionary<string, GatewayEntryOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["OpenRouter"] = new() { Enabled = openRouterEnabled },
                ["OmniRoute"] = new() { Enabled = omniRouteEnabled }
            }
        };

        var resolver = new ModelGatewayResolver(registry, options);

        return (resolver, openRouter, omniRoute);
    }

    [Fact]
    public void Resolve_WithExplicitRouteGatewayKey_ResolvesMatchingGateway()
    {
        var (resolver, _, omniRoute) = CreateResolver();
        var route = new ModelRoute("deepseek/deepseek-v4-flash-0731", GatewayKey: "OmniRoute");

        var resolution = resolver.Resolve(route);

        Assert.True(resolution.IsResolved);
        Assert.Equal("OmniRoute", resolution.GatewayKey);
        Assert.Same(omniRoute, resolution.Gateway);
        Assert.Null(resolution.FailureReason);
    }

    [Fact]
    public void Resolve_WithExplicitOpenRouterKey_ResolvesOpenRouter()
    {
        var (resolver, openRouter, _) = CreateResolver();
        var route = new ModelRoute("deepseek/deepseek-v4-flash-0731", GatewayKey: "OpenRouter");

        var resolution = resolver.Resolve(route);

        Assert.True(resolution.IsResolved);
        Assert.Equal("OpenRouter", resolution.GatewayKey);
        Assert.Same(openRouter, resolution.Gateway);
    }

    [Fact]
    public void Resolve_WithDisabledGateway_FailsClosedWithoutFallback()
    {
        var (resolver, _, _) = CreateResolver(openRouterEnabled: false);
        var route = new ModelRoute("deepseek/deepseek-v4-flash-0731", GatewayKey: "OpenRouter");

        var resolution = resolver.Resolve(route);

        Assert.False(resolution.IsResolved);
        Assert.Equal("OpenRouter", resolution.GatewayKey);
        Assert.Null(resolution.Gateway);
        Assert.Contains("disabled", resolution.FailureReason, StringComparison.OrdinalIgnoreCase);

        // Assert GetRequiredGateway throws ModelGatewayDisabledException
        Assert.Throws<ModelGatewayDisabledException>(() => resolver.GetRequiredGateway("OpenRouter"));
    }

    [Fact]
    public void Resolve_WithUnregisteredGateway_FailsClosedWithoutFallback()
    {
        var (resolver, _, _) = CreateResolver();
        var route = new ModelRoute("some-model", GatewayKey: "NonExistentGateway");

        var resolution = resolver.Resolve(route);

        Assert.False(resolution.IsResolved);
        Assert.Equal("NonExistentGateway", resolution.GatewayKey);
        Assert.Null(resolution.Gateway);
        Assert.Contains("not registered", resolution.FailureReason, StringComparison.OrdinalIgnoreCase);

        // Must NOT silently fallback to OpenRouter or OmniRoute
        Assert.Throws<ModelGatewayNotFoundException>(() => resolver.GetRequiredGateway("NonExistentGateway"));
    }

    [Fact]
    public void Resolve_WithNoGatewayKeyOnRoute_UsesDefaultGateway()
    {
        var (resolver, openRouter, _) = CreateResolver(defaultGateway: "OpenRouter");
        var route = new ModelRoute("deepseek/deepseek-v4-flash-0731", GatewayKey: null);

        var resolution = resolver.Resolve(route);

        Assert.True(resolution.IsResolved);
        Assert.Equal("OpenRouter", resolution.GatewayKey);
        Assert.Same(openRouter, resolution.Gateway);
    }

    [Fact]
    public void Resolve_WithNoGatewayKeyAndNoDefaultConfigured_FailsClosed()
    {
        var (resolver, _, _) = CreateResolver(defaultGateway: "");
        var route = new ModelRoute("deepseek/deepseek-v4-flash-0731", GatewayKey: null);

        var resolution = resolver.Resolve(route);

        Assert.False(resolution.IsResolved);
        Assert.Contains("No model gateway specified", resolution.FailureReason);
    }

    [Fact]
    public void Resolve_NullRoute_UsesDefaultGateway()
    {
        var (resolver, openRouter, _) = CreateResolver(defaultGateway: "OpenRouter");

        var resolution = resolver.Resolve((ModelRoute?)null);

        Assert.True(resolution.IsResolved);
        Assert.Equal("OpenRouter", resolution.GatewayKey);
        Assert.Same(openRouter, resolution.Gateway);
    }
}
