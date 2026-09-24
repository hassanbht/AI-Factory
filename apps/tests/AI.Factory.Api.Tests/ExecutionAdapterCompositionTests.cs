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
    public void Registry_ContainsExactlyOneAdapter_HermesExecutionAdapter()
    {
        using var scope = _factory.Services.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IExecutionAdapterRegistry>();

        Assert.Equal(new[] { "Hermes" }, registry.Keys);
        Assert.True(registry.TryGet("Hermes", out var adapter));
        Assert.IsType<HermesExecutionAdapter>(adapter);
        Assert.IsType<HermesExecutionAdapter>(Assert.Single(scope.ServiceProvider.GetServices<IExecutionAdapter>()));
    }

    [Fact]
    public void NoExecutionSection_ResolvesEveryAgentTypeToTheBuiltInHermesProfile()
    {
        using var scope = _factory.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<ExecutionOptions>();
        var resolver = scope.ServiceProvider.GetRequiredService<ExecutionProfileResolver>();

        Assert.Equal("Hermes", options.DefaultProfile);
        foreach (var agentType in new[] { "Implementation", "Repair", "Planner", "Review", "Documentation" })
        {
            var resolution = resolver.Resolve(agentType);
            Assert.True(resolution.IsResolved);
            Assert.Equal("Hermes", resolution.AdapterKey);
        }
    }

    [Fact]
    public void IAgentExecutor_StaysRegistered_ForTheAdapterAndForReview()
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
