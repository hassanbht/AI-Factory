using System.Reflection;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Execution;
using AI.Factory.Infrastructure.Agents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Execution-adapter Phases B/C — the real Api composition root (WebApplicationFactory
/// runs Program for real) resolves IAgentRuntime to the adapter dispatcher, with
/// exactly one registration and HermesExecutionAdapter as the only adapter behind
/// it, and wires Factory-side artifact recording into AgentOrchestrator.
/// Service resolution only — no agent is executed.
/// </summary>
public sealed class ExecutionAdapterCompositionTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public ExecutionAdapterCompositionTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void IAgentRuntime_IsTheAdapterDispatcher_RegisteredExactlyOnce()
    {
        using var scope = _factory.Services.CreateScope();

        var runtimes = scope.ServiceProvider.GetServices<IAgentRuntime>().ToArray();

        var runtime = Assert.Single(runtimes);
        Assert.IsType<AdapterDispatchingAgentRuntime>(runtime);
    }

    [Fact]
    public void Registry_ContainsHermesAndOpenHandsAdapters()
    {
        using var scope = _factory.Services.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IExecutionAdapterRegistry>();

        Assert.Equal(new[] { "Hermes", "OpenHands" }, registry.Keys);
        Assert.True(registry.TryGet("Hermes", out var hermesAdapter));
        Assert.IsType<HermesExecutionAdapter>(hermesAdapter);
        Assert.True(registry.TryGet("OpenHands", out var openHandsAdapter));
        Assert.IsType<AI.Factory.Infrastructure.Agents.OpenHands.OpenHandsExecutionAdapter>(openHandsAdapter);

        var adapters = scope.ServiceProvider.GetServices<IExecutionAdapter>().ToArray();
        Assert.Equal(2, adapters.Length);
        Assert.Contains(adapters, a => a is HermesExecutionAdapter);
        Assert.Contains(adapters, a => a is AI.Factory.Infrastructure.Agents.OpenHands.OpenHandsExecutionAdapter);
    }

    [Fact]
    public void ProductionConfiguration_ExplicitExecutionSection_ResolvesApprovedProfiles()
    {
        using var scope = _factory.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<ExecutionOptions>();
        var resolver = scope.ServiceProvider.GetRequiredService<ExecutionProfileResolver>();

        Assert.Equal("Hermes", options.DefaultProfile);
        Assert.True(options.Profiles.ContainsKey("Hermes"));
        Assert.Equal("Hermes", options.Profiles["Hermes"].Adapter);
        Assert.True(options.Profiles.ContainsKey("OpenHands"));
        Assert.Equal("OpenHands", options.Profiles["OpenHands"].Adapter);
        Assert.True(options.Profiles.ContainsKey("FastCoding"));
        Assert.Equal("OpenHands", options.Profiles["FastCoding"].Adapter);

        // Implementation -> OpenHands
        Assert.Equal("OpenHands", resolver.Resolve("Implementation").AdapterKey);
        // Repair -> OpenHands
        Assert.Equal("OpenHands", resolver.Resolve("Repair").AdapterKey);
        // Review -> Hermes (explicitly mapped in AgentTypeProfiles)
        Assert.Equal("Hermes", options.AgentTypeProfiles["Review"]);
        Assert.Equal("Hermes", resolver.Resolve("Review").AdapterKey);
        // Planner -> Hermes (unmapped falls back to DefaultProfile)
        Assert.Equal("Hermes", resolver.Resolve("Planner").AdapterKey);
        // Documentation -> Hermes (unmapped falls back to DefaultProfile)
        Assert.Equal("Hermes", resolver.Resolve("Documentation").AdapterKey);
    }

    [Fact]
    public void IAgentExecutionDispatcher_IsRegistered_AndResolvesToAdapterDispatchingAgentRuntime()
    {
        using var scope = _factory.Services.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<AI.Factory.Application.Abstractions.Execution.IAgentExecutionDispatcher>();

        Assert.IsType<AI.Factory.Application.Execution.AdapterDispatchingAgentRuntime>(dispatcher);
    }

    [Fact]
    public void ICodeReviewer_ResolvesToAgentCodeReviewer()
    {
        using var scope = _factory.Services.CreateScope();

        var reviewer = scope.ServiceProvider.GetRequiredService<AI.Factory.Application.Abstractions.ICodeReviewer>();

        Assert.IsType<AI.Factory.Application.Orchestration.AgentCodeReviewer>(reviewer);
    }

    [Fact]
    public void ConfigurationWithClearedExecutionSection_UsesTheBuiltInHermesProfile()
    {
        var options = new ExecutionOptions();
        options.ApplyBuiltInDefault("Hermes");
        options.Validate();

        using var scope = _factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IExecutionAdapterRegistry>();
        var resolver = new ExecutionProfileResolver(options, registry);

        Assert.Equal("Hermes", options.DefaultProfile);
        foreach (var agentType in new[] { "Implementation", "Repair", "Planner", "Review", "Documentation" })
        {
            var resolution = resolver.Resolve(agentType);
            Assert.True(resolution.IsResolved);
            Assert.Equal("Hermes", resolution.AdapterKey);
        }
    }

    [Fact]
    public void IAgentExecutor_StaysRegistered_ForTheAdapterAndPlanner()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.IsType<ResilienceAgentExecutorDecorator>(scope.ServiceProvider.GetRequiredService<IAgentExecutor>());
    }

    [Fact]
    public void AgentOrchestrator_RecordsArtifactsOnTheFactorySide()
    {
        using var scope = _factory.Services.CreateScope();

        var orchestrator = Assert.IsType<AgentOrchestrator>(scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>());

        Assert.NotNull(Field(orchestrator, "_artifactRecorder"));
        Assert.NotNull(Field(orchestrator, "_artifactRepository"));
    }

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
}
