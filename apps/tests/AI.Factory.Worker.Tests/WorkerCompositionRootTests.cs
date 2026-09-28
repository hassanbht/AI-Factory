using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Learning;
using AI.Factory.Application.Orchestration;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Builds the exact same service graph as the real Worker entry point
/// (<c>Program.ConfigureServices</c>) and resolves it, without ever calling
/// <c>host.Run()</c> — so hosted services are registered but never started.
/// This is the only test coverage of Worker's composition root: unlike the
/// API (indirectly exercised end-to-end by ApiIntegrationTests through
/// WebApplicationFactory), nothing else builds Worker's real DI container.
/// A missing/misconfigured registration here would otherwise only surface
/// at actual `dotnet run` startup.
/// </summary>
public sealed class WorkerCompositionRootTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IHost _host;

    public WorkerCompositionRootTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = FindWorkerProjectDirectory(),
            EnvironmentName = "Production"
        });

        Program.ConfigureServices(builder);

        // Swap the file-backed SQLite connection for an isolated in-memory
        // one, same isolation pattern ApiTestFactory uses for the API host.
        var descriptor = builder.Services.SingleOrDefault(
            d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));

        if (descriptor is not null)
        {
            builder.Services.Remove(descriptor);
        }

        builder.Services.AddDbContext<FactoryDbContext>(options => options.UseSqlite(_connection));

        _host = builder.Build();

        var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(schemaOptions);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _host.Dispose();
        _connection.Dispose();
    }

    private static string FindWorkerProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate AI.Factory.slnx above the test assembly.");
        }

        return Path.Combine(dir.FullName, "src", "AI.Factory.Worker");
    }

    [Theory]
    [InlineData(typeof(IProjectRepository))]
    [InlineData(typeof(ITaskStateManager))]
    [InlineData(typeof(IAgentExecutor))]
    [InlineData(typeof(IModelRouter))]
    [InlineData(typeof(IContextProvider))]
    [InlineData(typeof(IBuildExecutor))]
    [InlineData(typeof(ITestExecutor))]
    [InlineData(typeof(ICodeReviewer))]
    [InlineData(typeof(IEvaluationEngine))]
    [InlineData(typeof(ITaskPlanner))]
    [InlineData(typeof(IAuditLogger))]
    [InlineData(typeof(IOrchestrationRunRepository))]
    [InlineData(typeof(IOrchestrationProgressReporter))]
    [InlineData(typeof(ITaskDispatchScheduler))]
    [InlineData(typeof(IProjectCompletionGate))]
    [InlineData(typeof(IProjectWorkspaceProvisioner))]
    [InlineData(typeof(IProjectLifecycleOrchestrator))]
    [InlineData(typeof(IVerificationGate))]
    [InlineData(typeof(IWorkItemCompletionProcessor))]
    [InlineData(typeof(OrchestrationRunExecutionEngine))]
    [InlineData(typeof(LearningService))]
    [InlineData(typeof(IOperationContextAccessor))]
    [InlineData(typeof(IOperationScopeFactory))]
    [InlineData(typeof(IOperationalMetrics))]
    [InlineData(typeof(IFailureClassifier))]
    [InlineData(typeof(IOperationalDiagnosticsService))]
    [InlineData(typeof(IApplicationShutdown))]
    [InlineData(typeof(IBackgroundOperationTracker))]
    [InlineData(typeof(ShutdownOptions))]
    [InlineData(typeof(ISecretProvider))]
    [InlineData(typeof(IConfigurationDiagnosticsService))]
    [InlineData(typeof(AI.Factory.Application.Configuration.DatabaseOptions))]
    [InlineData(typeof(AI.Factory.Application.Configuration.DatabaseResilienceOptions))]
    [InlineData(typeof(AI.Factory.Application.Configuration.SecretManagementOptions))]
    [InlineData(typeof(AI.Factory.Application.Security.SecurityOptions))]
    [InlineData(typeof(AI.Factory.Application.Configuration.AiProviderOptions))]
    [InlineData(typeof(IDatabaseResiliencePolicy))]
    [InlineData(typeof(IDatabaseTransactionManager))]
    [InlineData(typeof(IDatabaseDiagnosticsService))]
    [InlineData(typeof(IDeploymentDiagnosticsService))]
    [InlineData(typeof(IWorkerHealthCoordinator))]
    public void RealWorkerServiceGraph_Resolves(Type serviceType)
    {
        using var scope = _host.Services.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService(serviceType);

        Assert.NotNull(resolved);
    }

    // Execution-adapter Phases B/C — IAgentRuntime is the adapter dispatcher (exactly
    // one registration; GoldenProjectLifecycleE2ETests relies on that), backed only
    // by HermesExecutionAdapter over the resilience-decorated IAgentExecutor chain.
    [Fact]
    public void IAgentRuntime_IsTheAdapterDispatcher_RegisteredExactlyOnce()
    {
        using var scope = _host.Services.CreateScope();

        var runtime = Assert.Single(scope.ServiceProvider.GetServices<IAgentRuntime>());

        Assert.IsType<AI.Factory.Application.Execution.AdapterDispatchingAgentRuntime>(runtime);
    }

    [Fact]
    public void ExecutionAdapterRegistry_ContainsHermesAndOpenHandsAdapters()
    {
        using var scope = _host.Services.CreateScope();

        var registry = scope.ServiceProvider
            .GetRequiredService<AI.Factory.Application.Abstractions.Execution.IExecutionAdapterRegistry>();

        Assert.Equal(new[] { "Hermes", "OpenHands" }, registry.Keys);
        Assert.True(registry.TryGet("Hermes", out var hermesAdapter));
        Assert.IsType<AI.Factory.Infrastructure.Agents.HermesExecutionAdapter>(hermesAdapter);
        Assert.True(registry.TryGet("OpenHands", out var openHandsAdapter));
        Assert.IsType<AI.Factory.Infrastructure.Agents.OpenHands.OpenHandsExecutionAdapter>(openHandsAdapter);
        var adapters = scope.ServiceProvider.GetServices<AI.Factory.Application.Abstractions.Execution.IExecutionAdapter>().ToArray();
        Assert.Equal(2, adapters.Length);
        Assert.IsType<AI.Factory.Infrastructure.Agents.ResilienceAgentExecutorDecorator>(
            scope.ServiceProvider.GetRequiredService<IAgentExecutor>());
    }

    [Fact]
    public void AgentOrchestrator_RecordsArtifactsOnTheFactorySide()
    {
        using var scope = _host.Services.CreateScope();

        var orchestrator = Assert.IsType<AI.Factory.Application.Agents.AgentOrchestrator>(
            scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>());

        foreach (var field in new[] { "_artifactRecorder", "_artifactRepository" })
        {
            Assert.NotNull(typeof(AI.Factory.Application.Agents.AgentOrchestrator)
                .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(orchestrator));
        }
    }

    [Fact]
    public void ProductionConfiguration_ExplicitExecutionSection_ResolvesApprovedProfiles()
    {
        using var scope = _host.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<AI.Factory.Application.Execution.ExecutionOptions>();
        var resolver = scope.ServiceProvider.GetRequiredService<AI.Factory.Application.Execution.ExecutionProfileResolver>();

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
    }

    [Fact]
    public void IAgentExecutionDispatcher_IsRegistered_AndResolvesToAdapterDispatchingAgentRuntime()
    {
        using var scope = _host.Services.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<AI.Factory.Application.Abstractions.Execution.IAgentExecutionDispatcher>();

        Assert.IsType<AI.Factory.Application.Execution.AdapterDispatchingAgentRuntime>(dispatcher);
    }

    [Fact]
    public void ICodeReviewer_ResolvesToAgentCodeReviewer()
    {
        using var scope = _host.Services.CreateScope();

        var reviewer = scope.ServiceProvider.GetRequiredService<AI.Factory.Application.Abstractions.ICodeReviewer>();

        Assert.IsType<AI.Factory.Application.Orchestration.AgentCodeReviewer>(reviewer);
    }

    [Fact]
    public void ConfigurationWithClearedExecutionSection_UsesTheBuiltInHermesProfile()
    {
        var options = new AI.Factory.Application.Execution.ExecutionOptions();
        options.ApplyBuiltInDefault("Hermes");
        options.Validate();

        using var scope = _host.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<AI.Factory.Application.Abstractions.Execution.IExecutionAdapterRegistry>();
        var resolver = new AI.Factory.Application.Execution.ExecutionProfileResolver(options, registry);

        Assert.Equal("Hermes", options.DefaultProfile);
        Assert.Equal("Hermes", Assert.Single(options.Profiles).Value.Adapter);
        Assert.Empty(options.AgentTypeProfiles);
        Assert.Equal("Hermes", resolver.Resolve("Implementation").AdapterKey);
    }

    [Fact]
    public void RealWorkerServiceGraph_RegistersBothHostedServices()
    {
        using var scope = _host.Services.CreateScope();

        var hostedServices = scope.ServiceProvider.GetServices<IHostedService>().ToList();

        Assert.Contains(hostedServices, s => s.GetType().Name == "WorkflowRecoveryHostedService");
        Assert.Contains(hostedServices, s => s.GetType().Name == "OrchestrationRunWorker");
    }

    [Fact]
    public void IOrchestrationService_IsIntentionallyNotRegistered_OnWorker()
    {
        // Worker only executes claimed runs — it never starts new ones, so
        // IOrchestrationService (Api's entry point for StartAsync) has no
        // reason to exist here. Asserting its absence documents that this
        // is a deliberate boundary, not an oversight.
        using var scope = _host.Services.CreateScope();

        var service = scope.ServiceProvider.GetService<IOrchestrationService>();

        Assert.Null(service);
    }
}
