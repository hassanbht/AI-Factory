using AI.Factory.Application.Execution;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

public sealed class ExecutionProfileResolverTests
{
    /// <summary>
    /// Mirrors the composition roots: configure, apply the built-in default, validate.
    /// Configuration *binding* of the same shapes is covered in
    /// AI.Factory.Worker.Tests.ExecutionConfigurationBindingTests (this project has
    /// no configuration binder reference, and none is added for it).
    /// </summary>
    private static ExecutionOptions Build(Action<ExecutionOptions>? configure = null)
    {
        var options = new ExecutionOptions();
        configure?.Invoke(options);
        options.ApplyBuiltInDefault("Hermes");
        options.Validate();
        return options;
    }

    private static ExecutionProfileOptions Adapter(string key) => new() { Adapter = key };

    private static ExecutionProfileResolver Resolver(ExecutionOptions options, params string[] registeredKeys) =>
        new(options, new ExecutionAdapterRegistry(registeredKeys.Select(k => new FakeExecutionAdapter(k))));

    [Fact]
    public void NoConfiguration_DefaultsToTheBuiltInHermesProfile()
    {
        var options = Build();

        Assert.Equal("Hermes", options.DefaultProfile);
        Assert.Equal("Hermes", options.Profiles["Hermes"].Adapter);

        var resolution = Resolver(options, "Hermes").Resolve("Implementation");

        Assert.True(resolution.IsResolved);
        Assert.Equal("Hermes", resolution.ProfileName);
        Assert.Equal("Hermes", resolution.Adapter!.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomeUnmappedType")]
    public void UnmappedOrMissingAgentType_UsesDefaultProfile(string? agentType)
    {
        var resolution = Resolver(Build(), "Hermes").Resolve(agentType);

        Assert.True(resolution.IsResolved);
        Assert.Equal("Hermes", resolution.AdapterKey);
    }

    [Fact]
    public void AgentType_ResolvesThroughItsConfiguredProfile()
    {
        var options = Build(o =>
        {
            o.DefaultProfile = "Standard";
            o.Profiles["Standard"] = Adapter("Hermes");
            o.Profiles["RepairProfile"] = Adapter("Hermes");
            o.AgentTypeProfiles["Repair"] = "RepairProfile";
        });

        var resolution = Resolver(options, "Hermes").Resolve("repair");

        Assert.True(resolution.IsResolved);
        Assert.Equal("RepairProfile", resolution.ProfileName);
        Assert.Equal("Hermes", resolution.Adapter!.Key);
    }

    [Fact]
    public void ConfiguredUnknownAdapterKey_FailsClosed_WithoutFallingBackToHermes()
    {
        var options = Build(o =>
        {
            o.DefaultProfile = "Hermes";
            o.Profiles["Hermes"] = Adapter("Hermes");
            o.Profiles["Next"] = Adapter("OpenHands");
            o.AgentTypeProfiles["Implementation"] = "Next";
        });

        var resolution = Resolver(options, "Hermes").Resolve("Implementation");

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Adapter);
        Assert.Equal("OpenHands", resolution.AdapterKey);
        Assert.Contains("not registered", resolution.FailureReason);
    }

    [Fact]
    public void DisabledAdapter_FailsClosed_EvenThoughItIsRegistered()
    {
        var options = Build(o => o.Adapters["Hermes"] = new ExecutionAdapterOptions { Enabled = false });

        var resolution = Resolver(options, "Hermes").Resolve("Implementation");

        Assert.False(resolution.IsResolved);
        Assert.Contains("disabled", resolution.FailureReason);
    }

    [Fact]
    public void ExplicitlyEnabledAdapter_Resolves()
    {
        var options = Build(o => o.Adapters["Hermes"] = new ExecutionAdapterOptions { Enabled = true });

        Assert.True(Resolver(options, "Hermes").Resolve("Implementation").IsResolved);
    }

    [Fact]
    public void UnknownProfile_AtResolutionTime_FailsClosed()
    {
        // Validate() rejects this shape at startup; the resolver must still fail
        // closed if options are ever mutated/constructed without validation.
        var options = Build();
        options.AgentTypeProfiles["Implementation"] = "Missing";

        var resolution = Resolver(options, "Hermes").Resolve("Implementation");

        Assert.False(resolution.IsResolved);
        Assert.Contains("'Missing' is not configured", resolution.FailureReason);
    }

    [Fact]
    public void BuiltInDefault_IsNotAppliedToAPartialSection()
    {
        var options = new ExecutionOptions();
        options.Profiles["Custom"] = new ExecutionProfileOptions { Adapter = "Hermes" };

        options.ApplyBuiltInDefault("Hermes");

        Assert.Equal(string.Empty, options.DefaultProfile);
        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("DefaultProfile is required", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_RejectsReferencesToUnconfiguredProfiles(bool viaDefaultProfile)
    {
        var options = new ExecutionOptions { DefaultProfile = viaDefaultProfile ? "Nope" : "Hermes" };
        options.Profiles["Hermes"] = Adapter("Hermes");
        if (!viaDefaultProfile)
        {
            options.AgentTypeProfiles["Implementation"] = "Nope";
        }

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("'Nope'", ex.Message);
    }

    [Fact]
    public void Validate_RejectsProfileWithoutAdapter()
    {
        var options = new ExecutionOptions { DefaultProfile = "P" };
        options.Profiles["P"] = new ExecutionProfileOptions { Adapter = "" };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Profile 'P' has no Adapter", ex.Message);
    }
}
