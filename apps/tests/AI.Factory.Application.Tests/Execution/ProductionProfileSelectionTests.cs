using AI.Factory.Application.Execution;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase G — Production Execution Profile Selection & Resolution Tests.
/// Validates the production execution profile design options:
///   Option 1: Hermes as default with explicit OpenHands profile.
///   Option 2: OpenHands as default with explicit Hermes profile.
///   Option 3: Role-based / task-class routing (e.g., Implementation -> OpenHands, Planner -> Hermes).
///   Option 4: Dual-runtime staged rollout (Stable -> Hermes, FastCoding -> OpenHands).
/// Enforces fail-closed invariants: no silent fallback between runtimes, deterministic resolution,
/// and complete configuration rollback safety.
/// </summary>
public sealed class ProductionProfileSelectionTests
{
    private static ExecutionProfileOptions Profile(string adapterKey) => new() { Adapter = adapterKey };

    private static ExecutionProfileResolver CreateResolver(ExecutionOptions options, params string[] registeredAdapters) =>
        new(options, new ExecutionAdapterRegistry(registeredAdapters.Select(k => new FakeExecutionAdapter(k))));

    [Fact]
    public void Option1_HermesDefault_AllowsExplicitOpenHandsProfile()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles =
            {
                ["Hermes"] = Profile("Hermes"),
                ["OpenHands"] = Profile("OpenHands")
            }
        };
        options.Validate();

        var resolver = CreateResolver(options, "Hermes", "OpenHands");

        // Default resolves Hermes
        var defaultRes = resolver.Resolve(null);
        Assert.True(defaultRes.IsResolved);
        Assert.Equal("Hermes", defaultRes.ProfileName);
        Assert.Equal("Hermes", defaultRes.AdapterKey);

        // Explicit unmapped agent types resolve Hermes
        var plannerRes = resolver.Resolve("Planner");
        Assert.True(plannerRes.IsResolved);
        Assert.Equal("Hermes", plannerRes.AdapterKey);
    }

    [Fact]
    public void Option2_OpenHandsDefault_AllowsExplicitHermesProfile()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "OpenHands",
            Profiles =
            {
                ["Hermes"] = Profile("Hermes"),
                ["OpenHands"] = Profile("OpenHands")
            }
        };
        options.Validate();

        var resolver = CreateResolver(options, "Hermes", "OpenHands");

        // Default resolves OpenHands
        var defaultRes = resolver.Resolve(null);
        Assert.True(defaultRes.IsResolved);
        Assert.Equal("OpenHands", defaultRes.ProfileName);
        Assert.Equal("OpenHands", defaultRes.AdapterKey);

        // Unmapped role resolves OpenHands
        var implRes = resolver.Resolve("Implementation");
        Assert.True(implRes.IsResolved);
        Assert.Equal("OpenHands", implRes.AdapterKey);
    }

    [Fact]
    public void Option3_RoleBasedRouting_RoutesImplementationAndRepairToOpenHands_AndPlannerToHermes()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles =
            {
                ["Hermes"] = Profile("Hermes"),
                ["OpenHands"] = Profile("OpenHands")
            },
            AgentTypeProfiles =
            {
                ["Implementation"] = "OpenHands",
                ["Repair"] = "OpenHands",
                ["Planner"] = "Hermes"
            }
        };
        options.Validate();

        var resolver = CreateResolver(options, "Hermes", "OpenHands");

        // Implementation -> OpenHands
        var implRes = resolver.Resolve("Implementation");
        Assert.True(implRes.IsResolved);
        Assert.Equal("OpenHands", implRes.ProfileName);
        Assert.Equal("OpenHands", implRes.AdapterKey);

        // Repair -> OpenHands
        var repairRes = resolver.Resolve("Repair");
        Assert.True(repairRes.IsResolved);
        Assert.Equal("OpenHands", repairRes.ProfileName);
        Assert.Equal("OpenHands", repairRes.AdapterKey);

        // Planner -> Hermes
        var plannerRes = resolver.Resolve("Planner");
        Assert.True(plannerRes.IsResolved);
        Assert.Equal("Hermes", plannerRes.ProfileName);
        Assert.Equal("Hermes", plannerRes.AdapterKey);

        // Unmapped role (e.g. Documentation) falls back to DefaultProfile (Hermes)
        var docRes = resolver.Resolve("Documentation");
        Assert.True(docRes.IsResolved);
        Assert.Equal("Hermes", docRes.ProfileName);
        Assert.Equal("Hermes", docRes.AdapterKey);
    }

    [Fact]
    public void Option4_DualRuntimeStagedProfiles_ResolvesNamedProfilesCleanly()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "Stable",
            Profiles =
            {
                ["Stable"] = Profile("Hermes"),
                ["FastCoding"] = Profile("OpenHands")
            },
            AgentTypeProfiles =
            {
                ["Implementation"] = "FastCoding",
                ["Planner"] = "Stable"
            }
        };
        options.Validate();

        var resolver = CreateResolver(options, "Hermes", "OpenHands");

        var implRes = resolver.Resolve("Implementation");
        Assert.True(implRes.IsResolved);
        Assert.Equal("FastCoding", implRes.ProfileName);
        Assert.Equal("OpenHands", implRes.AdapterKey);

        var plannerRes = resolver.Resolve("Planner");
        Assert.True(plannerRes.IsResolved);
        Assert.Equal("Stable", plannerRes.ProfileName);
        Assert.Equal("Hermes", plannerRes.AdapterKey);
    }

    [Fact]
    public void FailClosedInvariant_DisabledOpenHands_NeverFallsBackToHermes()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "OpenHands",
            Profiles =
            {
                ["Hermes"] = Profile("Hermes"),
                ["OpenHands"] = Profile("OpenHands")
            },
            Adapters =
            {
                ["OpenHands"] = new ExecutionAdapterOptions { Enabled = false }
            }
        };
        options.Validate();

        var resolver = CreateResolver(options, "Hermes", "OpenHands");
        var res = resolver.Resolve("Implementation");

        Assert.False(res.IsResolved);
        Assert.Null(res.Adapter);
        Assert.Equal("OpenHands", res.AdapterKey);
        Assert.Contains("disabled", res.FailureReason);
    }

    [Fact]
    public void FailClosedInvariant_UnregisteredAdapter_NeverFallsBackSilently()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "Custom",
            Profiles =
            {
                ["Custom"] = Profile("NonExistentAdapter")
            }
        };
        options.Validate();

        // Only Hermes registered
        var resolver = CreateResolver(options, "Hermes");
        var res = resolver.Resolve("Implementation");

        Assert.False(res.IsResolved);
        Assert.Null(res.Adapter);
        Assert.Equal("NonExistentAdapter", res.AdapterKey);
        Assert.Contains("not registered", res.FailureReason);
    }

    [Fact]
    public void RollbackSafety_TogglingDefaultProfile_IsCompletelyDeterministic()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles =
            {
                ["Hermes"] = Profile("Hermes"),
                ["OpenHands"] = Profile("OpenHands")
            }
        };
        options.Validate();

        var resolver = CreateResolver(options, "Hermes", "OpenHands");

        // Initial: Hermes
        Assert.Equal("Hermes", resolver.Resolve(null).AdapterKey);

        // Switch to OpenHands
        options.DefaultProfile = "OpenHands";
        options.Validate();
        Assert.Equal("OpenHands", resolver.Resolve(null).AdapterKey);

        // Roll back to Hermes
        options.DefaultProfile = "Hermes";
        options.Validate();
        Assert.Equal("Hermes", resolver.Resolve(null).AdapterKey);
    }
}
