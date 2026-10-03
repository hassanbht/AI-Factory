using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Execution;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase N — the four runtimes are selected only by explicit profile, and one runtime's selection, failure or
/// absence never changes what another AgentType (or the default) resolves to. There is no fallback chain.
/// </summary>
public sealed class CliRuntimeProfileResolutionTests
{
    private static readonly string[] AllRuntimes = { "Hermes", "OpenHands", "ClaudeCode", "Codex" };

    /// <summary>The shape the base Worker configuration declares: all four profiles, Hermes default, nothing mapped to the CLIs.</summary>
    private static ExecutionOptions Declared(Action<ExecutionOptions>? configure = null)
    {
        var options = new ExecutionOptions { DefaultProfile = "Hermes" };
        foreach (var runtime in AllRuntimes)
        {
            options.Profiles[runtime] = new ExecutionProfileOptions { Adapter = runtime };
        }

        configure?.Invoke(options);
        options.ApplyBuiltInDefault("Hermes");
        options.Validate();
        return options;
    }

    private static Dictionary<string, FakeExecutionAdapter> Adapters() =>
        AllRuntimes.ToDictionary(k => k, k => new FakeExecutionAdapter(k));

    private static ExecutionProfileResolver Resolver(ExecutionOptions options, IEnumerable<IExecutionAdapter> adapters) =>
        new(options, new ExecutionAdapterRegistry(adapters));

    private static AdapterDispatchingAgentRuntime Dispatcher(ExecutionOptions options, IEnumerable<IExecutionAdapter> adapters) =>
        new(Resolver(options, adapters), new FailureClassifier(), NullLogger<AdapterDispatchingAgentRuntime>.Instance);

    private static AgentRun Run(string agentType)
    {
        var definition = new AgentDefinition { Name = "agent", AgentType = agentType, SystemPrompt = "s", Enabled = true };
        return new AgentRun
        {
            AgentDefinitionId = definition.Id,
            AgentDefinition = definition,
            ProjectId = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid(),
            Model = "m-1",
            Provider = "gw-1",
            Input = "go",
            WorkspacePath = "/ws"
        };
    }

    [Theory]
    [InlineData("ClaudeCode")]
    [InlineData("Codex")]
    [InlineData("Hermes")]
    [InlineData("OpenHands")]
    public void ExplicitMapping_ResolvesToExactlyThatRuntimesAdapter(string runtime)
    {
        var options = Declared(o => o.AgentTypeProfiles["Implementation"] = runtime);

        var resolution = Resolver(options, Adapters().Values).Resolve("Implementation");

        Assert.True(resolution.IsResolved);
        Assert.Equal(runtime, resolution.ProfileName);
        Assert.Equal(runtime, resolution.AdapterKey);
        Assert.Equal(runtime, resolution.Adapter!.Key);
    }

    [Theory]
    [InlineData("ClaudeCode")]
    [InlineData("Codex")]
    public void MappingOneAgentTypeToACliRuntime_LeavesEveryOtherAgentTypeAndTheDefaultUntouched(string runtime)
    {
        var baseline = Declared();
        var changed = Declared(o => o.AgentTypeProfiles["Implementation"] = runtime);
        var adapters = Adapters().Values;

        foreach (var agentType in new[] { "Repair", "Review", "Planner", "Architect", "SomethingElse", null })
        {
            var before = Resolver(baseline, adapters).Resolve(agentType);
            var after = Resolver(changed, adapters).Resolve(agentType);

            Assert.Equal(before.AdapterKey, after.AdapterKey);
            Assert.Equal("Hermes", after.AdapterKey);
        }

        Assert.Equal("Hermes", changed.DefaultProfile);
    }

    [Fact]
    public void DeclaringTheCliProfiles_WithoutMappingThem_ChangesNoAgentTypesRuntime()
    {
        var options = Declared();
        var resolver = Resolver(options, Adapters().Values);

        foreach (var agentType in new[] { "Implementation", "Repair", "Review", "Planner" })
        {
            Assert.Equal("Hermes", resolver.Resolve(agentType).AdapterKey);
        }
    }

    [Theory]
    [InlineData("ClaudeCode")]
    [InlineData("Codex")]
    public void DisabledCliAdapter_FailsClosed_AndNeverResolvesAnotherRuntime(string runtime)
    {
        var options = Declared(o =>
        {
            o.AgentTypeProfiles["Implementation"] = runtime;
            o.Adapters[runtime] = new ExecutionAdapterOptions { Enabled = false };
        });

        var resolution = Resolver(options, Adapters().Values).Resolve("Implementation");

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Adapter);
        Assert.Equal(runtime, resolution.AdapterKey);
        Assert.Contains("disabled", resolution.FailureReason);
    }

    [Theory]
    [InlineData("ClaudeCode")]
    [InlineData("Codex")]
    public void UnregisteredCliAdapter_FailsClosed_AndNeverResolvesAnotherRuntime(string runtime)
    {
        var options = Declared(o => o.AgentTypeProfiles["Implementation"] = runtime);
        var others = Adapters().Where(kv => kv.Key != runtime).Select(kv => (IExecutionAdapter)kv.Value);

        var resolution = Resolver(options, others).Resolve("Implementation");

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Adapter);
        Assert.Contains("not registered", resolution.FailureReason);
    }

    [Theory]
    [InlineData("ClaudeCode", ExecutionOutcomeStatus.Failed, FailureCategory.Provider)]
    [InlineData("ClaudeCode", ExecutionOutcomeStatus.TimedOut, FailureCategory.Timeout)]
    [InlineData("Codex", ExecutionOutcomeStatus.Failed, FailureCategory.Provider)]
    [InlineData("Codex", ExecutionOutcomeStatus.TimedOut, FailureCategory.Timeout)]
    public async Task FailingCliRuntime_IsNeverRetriedOnAnotherRuntime_ByTheDispatcher(
        string runtime, ExecutionOutcomeStatus status, FailureCategory category)
    {
        var adapters = Adapters();
        adapters[runtime] = new FakeExecutionAdapter(runtime, execute: (spec, _) =>
            Task.FromResult(FakeExecutionAdapter.Outcome(status, spec, output: null, error: "runtime failed", category: category)));
        var options = Declared(o => o.AgentTypeProfiles["Implementation"] = runtime);

        var result = await Dispatcher(options, adapters.Values).ExecuteAsync(Run("Implementation"));

        Assert.False(result.Success);
        Assert.Equal(1, adapters[runtime].CallCount);
        Assert.All(adapters.Where(kv => kv.Key != runtime), kv => Assert.Equal(0, kv.Value.CallCount));
        Assert.Equal(runtime, result.ExecutionAdapter);
    }

    [Fact]
    public async Task ThrowingCliRuntime_PropagatesTheException_WithoutTouchingAnotherAdapter()
    {
        var adapters = Adapters();
        adapters["ClaudeCode"] = new FakeExecutionAdapter("ClaudeCode", execute: (_, _) => throw new InvalidOperationException("boom"));
        var options = Declared(o => o.AgentTypeProfiles["Implementation"] = "ClaudeCode");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatcher(options, adapters.Values).ExecuteAsync(Run("Implementation")));

        Assert.All(adapters.Where(kv => kv.Key != "ClaudeCode"), kv => Assert.Equal(0, kv.Value.CallCount));
    }

    [Fact]
    public void NoExecutionOptionOrResolverApi_ExpressesAFallbackChain()
    {
        // Structural guard: a fallback would need somewhere to be declared. Neither the profile nor the adapter
        // options have any property naming another adapter/profile to try next.
        var names = typeof(ExecutionProfileOptions).GetProperties().Select(p => p.Name)
            .Concat(typeof(ExecutionAdapterOptions).GetProperties().Select(p => p.Name))
            .Concat(typeof(ExecutionOptions).GetProperties().Select(p => p.Name))
            .ToArray();

        Assert.DoesNotContain(names, n => n.Contains("Fallback", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Failover", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Secondary", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Backup", StringComparison.OrdinalIgnoreCase));
    }
}
