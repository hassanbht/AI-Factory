using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Execution;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase H §8 & §19 — Profile Independence Tests.
/// Proves that the Review profile is explicit and completely decoupled from
/// Implementation and Repair routing:
/// - Implementation -> OpenHands
/// - Repair         -> OpenHands
/// - Review         -> Hermes
/// Changes to Implementation or Repair must never affect Review.
/// Changes to Review must never affect Implementation or Repair.
/// No silent fallback may occur across profiles or runtimes.
/// </summary>
public sealed class ReviewProfileIndependenceTests
{
    private sealed class StubAdapter : IExecutionAdapter
    {
        public StubAdapter(string key) => Key = key;
        public string Key { get; }
        public ExecutionCapabilities Capabilities => ExecutionCapabilities.FactoryRoutedModel;
        public Task<ExecutionOutcome> ExecuteAsync(AgentExecutionSpec spec, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private static (ExecutionOptions options, ExecutionProfileResolver resolver) CreateResolver(
        Action<ExecutionOptions>? configure = null)
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles =
            {
                ["Hermes"] = new ExecutionProfileOptions { Adapter = "Hermes" },
                ["OpenHands"] = new ExecutionProfileOptions { Adapter = "OpenHands" },
                ["FastCoding"] = new ExecutionProfileOptions { Adapter = "OpenHands" },
                ["DedicatedReview"] = new ExecutionProfileOptions { Adapter = "Hermes" }
            },
            AgentTypeProfiles =
            {
                ["Implementation"] = "OpenHands",
                ["Repair"] = "OpenHands",
                ["Review"] = "Hermes"
            }
        };

        configure?.Invoke(options);
        options.Validate();

        var registry = new ExecutionAdapterRegistry(
            new[] { new StubAdapter("Hermes"), new StubAdapter("OpenHands") });

        var resolver = new ExecutionProfileResolver(options, registry);
        return (options, resolver);
    }

    [Fact]
    public void ProductionConfiguration_MaintainsExplicitDecoupledProfiles()
    {
        var (options, resolver) = CreateResolver();

        // 1. Verify exact configuration mapping
        Assert.Equal("Hermes", options.DefaultProfile);
        Assert.Equal("OpenHands", options.AgentTypeProfiles["Implementation"]);
        Assert.Equal("OpenHands", options.AgentTypeProfiles["Repair"]);
        Assert.Equal("Hermes", options.AgentTypeProfiles["Review"]);

        // 2. Verify deterministic runtime resolution
        var implRes = resolver.Resolve("Implementation");
        Assert.True(implRes.IsResolved);
        Assert.Equal("OpenHands", implRes.ProfileName);
        Assert.Equal("OpenHands", implRes.AdapterKey);

        var repairRes = resolver.Resolve("Repair");
        Assert.True(repairRes.IsResolved);
        Assert.Equal("OpenHands", repairRes.ProfileName);
        Assert.Equal("OpenHands", repairRes.AdapterKey);

        var reviewRes = resolver.Resolve("Review");
        Assert.True(reviewRes.IsResolved);
        Assert.Equal("Hermes", reviewRes.ProfileName);
        Assert.Equal("Hermes", reviewRes.AdapterKey);

        // 3. Verify unmapped profiles fall back to DefaultProfile (Hermes)
        var plannerRes = resolver.Resolve("Planner");
        Assert.True(plannerRes.IsResolved);
        Assert.Equal("Hermes", plannerRes.ProfileName);
        Assert.Equal("Hermes", plannerRes.AdapterKey);

        var docRes = resolver.Resolve("Documentation");
        Assert.True(docRes.IsResolved);
        Assert.Equal("Hermes", docRes.ProfileName);
        Assert.Equal("Hermes", docRes.AdapterKey);
    }

    [Fact]
    public void ChangingReviewProfile_DoesNotAffectImplementationOrRepair()
    {
        var (_, resolver) = CreateResolver(options =>
        {
            options.AgentTypeProfiles["Review"] = "DedicatedReview";
        });

        // Review now resolves through DedicatedReview profile
        var reviewRes = resolver.Resolve("Review");
        Assert.True(reviewRes.IsResolved);
        Assert.Equal("DedicatedReview", reviewRes.ProfileName);
        Assert.Equal("Hermes", reviewRes.AdapterKey);

        // Implementation and Repair MUST remain completely unaffected on OpenHands
        var implRes = resolver.Resolve("Implementation");
        Assert.Equal("OpenHands", implRes.ProfileName);
        Assert.Equal("OpenHands", implRes.AdapterKey);

        var repairRes = resolver.Resolve("Repair");
        Assert.Equal("OpenHands", repairRes.ProfileName);
        Assert.Equal("OpenHands", repairRes.AdapterKey);
    }

    [Fact]
    public void ChangingImplementationProfile_DoesNotAffectReview()
    {
        var (_, resolver) = CreateResolver(options =>
        {
            options.AgentTypeProfiles["Implementation"] = "Hermes";
            options.AgentTypeProfiles["Repair"] = "FastCoding";
        });

        // Implementation and Repair changed
        Assert.Equal("Hermes", resolver.Resolve("Implementation").AdapterKey);
        Assert.Equal("OpenHands", resolver.Resolve("Repair").AdapterKey);

        // Review MUST remain on its own explicit profile
        var reviewRes = resolver.Resolve("Review");
        Assert.Equal("Hermes", reviewRes.ProfileName);
        Assert.Equal("Hermes", reviewRes.AdapterKey);
    }

    [Fact]
    public void UnknownAdapterForReview_FailsClosed_WithoutSilentFallback()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles =
            {
                ["Hermes"] = new ExecutionProfileOptions { Adapter = "Hermes" },
                ["BrokenReview"] = new ExecutionProfileOptions { Adapter = "NonExistentRuntime" }
            },
            AgentTypeProfiles =
            {
                ["Review"] = "BrokenReview"
            }
        };
        options.Validate();

        var registry = new ExecutionAdapterRegistry(
            new[] { new StubAdapter("Hermes") });
        var resolver = new ExecutionProfileResolver(options, registry);

        var resolution = resolver.Resolve("Review");

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Adapter);
        Assert.Contains("NonExistentRuntime", resolution.FailureReason);
        Assert.Contains("not registered", resolution.FailureReason);
    }

    [Fact]
    public void DisabledAdapterForReview_FailsClosed_WithoutSilentFallback()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles =
            {
                ["Hermes"] = new ExecutionProfileOptions { Adapter = "Hermes" }
            },
            AgentTypeProfiles =
            {
                ["Review"] = "Hermes"
            },
            Adapters =
            {
                ["Hermes"] = new ExecutionAdapterOptions { Enabled = false }
            }
        };
        options.Validate();

        var registry = new ExecutionAdapterRegistry(
            new[] { new StubAdapter("Hermes") });
        var resolver = new ExecutionProfileResolver(options, registry);

        var resolution = resolver.Resolve("Review");

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Adapter);
        Assert.Contains("disabled", resolution.FailureReason);
    }
}
