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
