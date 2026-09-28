using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Tests.Execution;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

public sealed class GatewayProfileIndependenceTests
{
    private static (ExecutionProfileResolver Resolver, ExecutionOptions Options) CreateResolver()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles = new Dictionary<string, ExecutionProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Hermes"] = new() { Adapter = "Hermes", Gateway = "OpenRouter" },
                ["OpenHands"] = new() { Adapter = "OpenHands", Gateway = "OpenRouter" },
                ["FastCoding"] = new() { Adapter = "OpenHands", Gateway = "OmniRoute" },
                ["CustomReview"] = new() { Adapter = "Hermes", Gateway = "OmniRoute" }
            },
            AgentTypeProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Implementation"] = "OpenHands",
                ["Repair"] = "OpenHands",
                ["Review"] = "Hermes"
            }
        };

        var hermes = new FakeExecutionAdapter("Hermes");
        var openHands = new FakeExecutionAdapter("OpenHands");
        var registry = new ExecutionAdapterRegistry(new[] { hermes, openHands });

        var resolver = new ExecutionProfileResolver(options, registry);
        return (resolver, options);
    }

    [Fact]
    public void AgentProfiles_ResolveAdapterAndGateway_Independently()
    {
        var (resolver, _) = CreateResolver();

        var impl = resolver.Resolve("Implementation");
        Assert.Equal("OpenHands", impl.AdapterKey);
        Assert.Equal("OpenRouter", impl.GatewayKey);

        var repair = resolver.Resolve("Repair");
        Assert.Equal("OpenHands", repair.AdapterKey);
        Assert.Equal("OpenRouter", repair.GatewayKey);

        var review = resolver.Resolve("Review");
        Assert.Equal("Hermes", review.AdapterKey);
        Assert.Equal("OpenRouter", review.GatewayKey);

        // Planner falls back to default profile (Hermes)
        var planner = resolver.Resolve("Planner");
        Assert.Equal("Hermes", planner.AdapterKey);
        Assert.Equal("OpenRouter", planner.GatewayKey);
    }

    [Fact]
    public void ModifyingImplementationProfileGateway_DoesNotAffectReviewOrRepair()
    {
        var (resolver, options) = CreateResolver();

        // Change Implementation profile to FastCoding (which uses OmniRoute)
        options.AgentTypeProfiles["Implementation"] = "FastCoding";

        var impl = resolver.Resolve("Implementation");
        Assert.Equal("OpenHands", impl.AdapterKey);
        Assert.Equal("OmniRoute", impl.GatewayKey);

        var repair = resolver.Resolve("Repair");
        Assert.Equal("OpenHands", repair.AdapterKey);
        Assert.Equal("OpenRouter", repair.GatewayKey); // Unchanged!

        var review = resolver.Resolve("Review");
        Assert.Equal("Hermes", review.AdapterKey);
        Assert.Equal("OpenRouter", review.GatewayKey); // Unchanged!
    }

    [Fact]
    public void ModifyingReviewProfileGateway_DoesNotAffectImplementationOrRepair()
    {
        var (resolver, options) = CreateResolver();

        // Change Review to CustomReview (which uses OmniRoute)
        options.AgentTypeProfiles["Review"] = "CustomReview";

        var review = resolver.Resolve("Review");
        Assert.Equal("Hermes", review.AdapterKey);
        Assert.Equal("OmniRoute", review.GatewayKey);

        var impl = resolver.Resolve("Implementation");
        Assert.Equal("OpenHands", impl.AdapterKey);
        Assert.Equal("OpenRouter", impl.GatewayKey); // Unchanged!

        var repair = resolver.Resolve("Repair");
        Assert.Equal("OpenHands", repair.AdapterKey);
        Assert.Equal("OpenRouter", repair.GatewayKey); // Unchanged!
    }
}
