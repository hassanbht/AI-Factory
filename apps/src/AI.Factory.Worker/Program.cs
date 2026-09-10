using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Approvals;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Learning;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Security;
using AI.Factory.Application.Tools;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Configuration;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Providers;
using AI.Factory.Infrastructure.Resilience;
using AI.Factory.Application.Resilience;
using AI.Factory.Infrastructure.Diagnostics;
using AI.Factory.Infrastructure.Shutdown;
using AI.Factory.Infrastructure.Validation;
using AI.Factory.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
Program.ConfigureServices(builder);

var host = builder.Build();
host.Run();

public partial class Program
{
    // Extracted so a composition-root test can build the exact same service
    // graph and resolve it, without invoking host.Run() (which blocks and
    // starts the hosted services). See AI.Factory.Worker.Tests.
    public static void ConfigureServices(HostApplicationBuilder builder)
    {
        var securityOptions = new SecurityOptions();
        builder.Configuration.GetSection(SecurityOptions.SectionName).Bind(securityOptions);
        securityOptions.Validate();
        builder.Services.AddSingleton(securityOptions);

        var databaseOptions = new DatabaseOptions();
        builder.Configuration.GetSection(DatabaseOptions.SectionName).Bind(databaseOptions);
        if (string.IsNullOrWhiteSpace(databaseOptions.ConnectionString))
        {
            databaseOptions.ConnectionString =
                builder.Configuration.GetConnectionString("FactoryDatabase")
                ?? "Data Source=../../../data/factory.db";
        }
        databaseOptions.Validate();
        builder.Services.AddSingleton(databaseOptions);

        var databaseResilienceOptions = new DatabaseResilienceOptions();
        builder.Configuration.GetSection(DatabaseResilienceOptions.SectionName).Bind(databaseResilienceOptions);
        databaseResilienceOptions.Validate();
        builder.Services.AddSingleton(databaseResilienceOptions);

        var secretOptions = new SecretManagementOptions();
        builder.Configuration.GetSection(SecretManagementOptions.SectionName).Bind(secretOptions);
        secretOptions.Validate();
        builder.Services.AddSingleton(secretOptions);

        var aiProviderOptions = new AiProviderOptions();
        builder.Configuration.GetSection(AiProviderOptions.SectionName).Bind(aiProviderOptions);
        aiProviderOptions.Validate();
        builder.Services.AddSingleton(aiProviderOptions);

        builder.Services.AddSingleton<ISecretProvider, EnvironmentSecretProvider>();
        builder.Services.AddSingleton<IConfigurationDiagnosticsService, ConfigurationDiagnosticsService>();
        builder.Services.AddSingleton<IDatabaseResiliencePolicy, DatabaseResiliencePolicy>();
        builder.Services.AddScoped<IDatabaseTransactionManager, DatabaseTransactionManager>();
        builder.Services.AddScoped<IDatabaseDiagnosticsService, DatabaseDiagnosticsService>();
        builder.Services.AddSingleton<IDeploymentDiagnosticsService, DeploymentDiagnosticsService>();
        var workerHealthOptions = new WorkerHealthOptions();
        builder.Configuration.GetSection(WorkerHealthOptions.SectionName).Bind(workerHealthOptions);
        workerHealthOptions.Validate();
        builder.Services.AddSingleton(workerHealthOptions);
        builder.Services.AddSingleton<IWorkerHealthCoordinator, WorkerHealthCoordinator>();
        builder.Services.AddHostedService<WorkerHealthLifecycleHostedService>();

        builder.Services.AddDbContext<FactoryDbContext>(options =>
        {
            options.UseSqlite(databaseOptions.ConnectionString, sqliteOptions =>
            {
                sqliteOptions.CommandTimeout((int)databaseResilienceOptions.CommandTimeout.TotalSeconds);
            });
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });

        var executionLimits = new ExecutionLimitsOptions();
        builder.Configuration.GetSection("ExecutionLimits").Bind(executionLimits);
        executionLimits.Validate();
        builder.Services.AddSingleton(executionLimits);

var recoveryOptions = new RecoveryOptions();
builder.Configuration.GetSection("Recovery").Bind(recoveryOptions);
builder.Services.AddSingleton(recoveryOptions);

builder.Services.AddScoped<IProjectRepository, ProjectRepository>();

builder.Services.AddScoped<IWorkflowStateStore, WorkflowStateStore>();
builder.Services.AddSingleton<IWorkflowTransitionValidator, WorkflowTransitionValidator>();
builder.Services.AddScoped<IWorkflowEngine, WorkflowEngine>();

builder.Services.AddScoped<IWorkItemRepository, WorkItemRepository>();
builder.Services.AddSingleton<ITaskDependencyResolver, TaskDependencyResolver>();
builder.Services.AddScoped<ITaskStateManager, TaskStateManager>();
builder.Services.AddScoped<ITaskScheduler, WorkItemTaskScheduler>();

var hermesOptions = new HermesOptions();
builder.Configuration.GetSection("Hermes").Bind(hermesOptions);

if (string.IsNullOrWhiteSpace(hermesOptions.DataVolume))
{
    hermesOptions.DataVolume = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes");
}

if (string.IsNullOrWhiteSpace(hermesOptions.WorkspaceRoot))
{
    hermesOptions.WorkspaceRoot = "../../../sandbox/agent-workspace";
}

builder.Services.AddSingleton(hermesOptions);

var commandPolicyOptions = new CommandPolicyOptions();
builder.Configuration.GetSection("CommandPolicy").Bind(commandPolicyOptions);
builder.Services.AddSingleton(commandPolicyOptions);
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddScoped<HermesAgentExecutor>();

var resilienceOptions = new ResilienceOptions();
builder.Configuration.GetSection(ResilienceOptions.SectionName).Bind(resilienceOptions);
resilienceOptions.Validate();
builder.Services.AddSingleton(resilienceOptions);
builder.Services.AddSingleton<IBackoffCalculator, ExponentialBackoffCalculator>();
builder.Services.AddSingleton<IDelayProvider, DefaultDelayProvider>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICircuitBreakerRegistry, CircuitBreakerRegistry>();
builder.Services.AddSingleton<IBulkheadRegistry, BulkheadRegistry>();
builder.Services.AddScoped<IResilienceExecutor, ResilienceExecutor>();

// Decorates HermesAgentExecutor with centralized resilience (bulkhead, circuit breaker, retry, and timeout)
builder.Services.AddScoped<IAgentExecutor>(sp =>
{
    var hermes = sp.GetRequiredService<HermesAgentExecutor>();
    return new ResilienceAgentExecutorDecorator(
        hermes,
        sp.GetRequiredService<IResilienceExecutor>(),
        sp.GetRequiredService<IFailureClassifier>(),
        sp.GetRequiredService<ILogger<ResilienceAgentExecutorDecorator>>());
});

// Phase 18.1.3 — first real IAgentRuntime, adapting the canonical AgentRun
// model onto the same (already resilience-decorated) Hermes execution path
// above. Does not replace or bypass IAgentExecutor.
builder.Services.AddScoped<IAgentRuntime, HermesAgentRuntime>();

var modelRoutingOptions = new ModelRoutingOptions();
builder.Configuration.GetSection("ModelRouting").Bind(modelRoutingOptions);
builder.Services.AddSingleton(modelRoutingOptions);
builder.Services.AddSingleton<IModelRouter, ModelRouter>();

// ------------------------------------------------------------
// AI provider abstraction & routing (Phase 18.1.1) — provider-neutral
// execution built on top of the AiProviders/ModelRouting catalogs above.
// Each enabled provider is wrapped with the same ProviderCall resilience
// profile used elsewhere, then registered into a scoped, in-memory registry.
// ------------------------------------------------------------

builder.Services.AddHttpClient();

foreach (var (providerId, providerSettings) in aiProviderOptions.Providers)
{
    if (!providerSettings.Enabled)
    {
        continue;
    }

    builder.Services.AddHttpClient(providerId, client =>
    {
        if (!string.IsNullOrWhiteSpace(providerSettings.BaseUrl))
        {
            client.BaseAddress = new Uri(providerSettings.BaseUrl.TrimEnd('/') + "/");
        }

        client.Timeout = TimeSpan.FromSeconds(providerSettings.TimeoutSeconds);
    });
}

builder.Services.AddScoped<IAiModelProviderRegistry>(sp =>
{
    var registry = new AiModelProviderRegistry();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var secretProvider = sp.GetRequiredService<ISecretProvider>();
    var resilienceExecutor = sp.GetRequiredService<IResilienceExecutor>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    foreach (var (providerId, providerSettings) in aiProviderOptions.Providers)
    {
        if (!providerSettings.Enabled)
        {
            continue;
        }

        var providerCapabilities = modelRoutingOptions.Models
            .Where(m => string.Equals(m.Provider, providerId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => m.Capabilities)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IModelProvider inner = providerSettings.ProviderType == AiModelProviderKind.Fake
            ? new FakeModelProvider(providerId, providerCapabilities)
            : new OpenAiCompatibleModelProvider(
                providerId,
                providerCapabilities,
                httpClientFactory.CreateClient(providerId),
                secretProvider,
                providerSettings.ApiKeySecretName,
                loggerFactory.CreateLogger<OpenAiCompatibleModelProvider>());

        registry.RegisterProvider(new ResilienceModelProviderDecorator(
            inner, resilienceExecutor, loggerFactory.CreateLogger<ResilienceModelProviderDecorator>()));
    }

    foreach (var model in modelRoutingOptions.Models)
    {
        if (model.Capabilities.Count == 0)
        {
            continue;
        }

        if (registry.TryGetProvider(model.Provider, out _))
        {
            registry.RegisterModel(model.ModelId, model.Provider);
        }
    }

    return registry;
});

var aiRoutingOptions = new AiRoutingOptions();
builder.Configuration.GetSection(AiRoutingOptions.SectionName).Bind(aiRoutingOptions);
aiRoutingOptions.Validate();
builder.Services.AddSingleton(aiRoutingOptions);
builder.Services.AddScoped<IAiModelRouter, AiModelRouter>();

var retryOptions = new RetryOptions();
builder.Configuration.GetSection("Retry").Bind(retryOptions);
builder.Services.AddSingleton(retryOptions);
builder.Services.AddSingleton<RetryExecutor>();

var openVikingOptions = new OpenVikingOptions();
builder.Configuration.GetSection("OpenViking").Bind(openVikingOptions);
builder.Services.AddSingleton(openVikingOptions);

if (openVikingOptions.Enabled)
{
    builder.Services.AddHttpClient<IOpenVikingClient, HttpOpenVikingClient>(client =>
    {
        client.BaseAddress = new Uri(openVikingOptions.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(openVikingOptions.TimeoutSeconds);
    });
    builder.Services.AddScoped<IContextProvider, OpenVikingContextProvider>();
}
else
{
    builder.Services.AddScoped<IContextProvider, LocalContextProvider>();
}

builder.Services.AddScoped<IHermesExecutionRepository, HermesExecutionRepository>();
builder.Services.AddScoped<IAgentDefinitionRepository, AgentDefinitionRepository>();
builder.Services.AddScoped<IAgentRunRepository, AgentRunRepository>();
builder.Services.AddScoped<IArtifactRecorder, FileSystemArtifactRecorder>();

builder.Services.AddScoped<IBuildExecutor, DotNetBuildExecutor>();
builder.Services.AddScoped<ITestExecutor, DotNetTestExecutor>();
builder.Services.AddScoped<IValidationEngine, ValidationEngine>();
builder.Services.AddScoped<IValidationRunRepository, ValidationRunRepository>();

var repairOptions = new RepairOptions();
builder.Configuration.GetSection("Repair").Bind(repairOptions);
builder.Services.AddSingleton(repairOptions);

builder.Services.AddScoped<IArtifactRepository, ArtifactRepository>();

// Phase 18.1.4 — Agent Tools & Memory Layer. Tools are collected by IEnumerable<IAgentTool>
// into the registry; add new built-in tools by registering another IAgentTool here.
builder.Services.AddScoped<IAgentTool, ReadArtifactTool>();
builder.Services.AddScoped<IAgentToolRegistry, AgentToolRegistry>();
builder.Services.AddScoped<IAgentMemory, LocalAgentMemoryProvider>();

// Phase 18.1.5 — Agent Orchestration Service + Worker.
builder.Services.AddScoped<IAgentInstructionBuilder, AgentInstructionBuilder>();
builder.Services.AddSingleton<IStructuredAgentResultParser, StructuredAgentResultParser>();
builder.Services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();

var agentWorkerOptions = new AgentWorkerOptions();
builder.Configuration.GetSection("AgentWorker").Bind(agentWorkerOptions);
agentWorkerOptions.Validate();
builder.Services.AddSingleton(agentWorkerOptions);

builder.Services.AddScoped<IReviewRunRepository, ReviewRunRepository>();
builder.Services.AddScoped<IReviewContextBuilder, ReviewContextBuilder>();
builder.Services.AddScoped<ICodeReviewer, AgentCodeReviewer>();

var reviewOptions = new ReviewOptions();
builder.Configuration.GetSection("Review").Bind(reviewOptions);
builder.Services.AddSingleton(reviewOptions);

builder.Services.AddScoped<IApprovalRepository, ApprovalRepository>();
builder.Services.AddScoped<ApprovalService>();

builder.Services.AddScoped<IEvaluationEngine, EvaluationEngine>();
builder.Services.AddScoped<IEvaluationRunRepository, EvaluationRunRepository>();
builder.Services.AddScoped<ILearningStore, LearningStore>();
builder.Services.AddScoped<LearningService>();

var taskPlannerOptions = new ModelTaskPlannerOptions();
builder.Configuration.GetSection("TaskPlanner").Bind(taskPlannerOptions);
builder.Services.AddSingleton(taskPlannerOptions);
builder.Services.AddScoped<ITaskPlanner, ModelTaskPlanner>();

var auditOptions = new AuditOptions();
builder.Configuration.GetSection("Audit").Bind(auditOptions);
builder.Services.AddSingleton(auditOptions);
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();

builder.Services.AddSingleton<OperationContextAccessor>();
builder.Services.AddSingleton<IOperationContextAccessor>(sp => sp.GetRequiredService<OperationContextAccessor>());
builder.Services.AddSingleton<IOperationalMetrics, OperationalMetrics>();
builder.Services.AddSingleton<IBackgroundOperationTracker, BackgroundOperationTracker>();
builder.Services.AddSingleton<IApplicationShutdown, ApplicationShutdownCoordinator>();
builder.Services.AddSingleton<IOperationScopeFactory, OperationScopeFactory>();
builder.Services.AddSingleton<IFailureClassifier, FailureClassifier>();
builder.Services.AddScoped<IOperationalDiagnosticsService, OperationalDiagnosticsService>();

var shutdownOptions = new ShutdownOptions();
builder.Configuration.GetSection(ShutdownOptions.SectionName).Bind(shutdownOptions);
shutdownOptions.Validate();
builder.Services.AddSingleton(shutdownOptions);

var orchestrationOptions = new OrchestrationOptions();
builder.Configuration.GetSection("Orchestration").Bind(orchestrationOptions);

if (string.IsNullOrWhiteSpace(orchestrationOptions.WorkspaceRoot))
{
    orchestrationOptions.WorkspaceRoot = "../../../sandbox/agent-workspace";
}

orchestrationOptions.Validate();
builder.Services.AddSingleton(orchestrationOptions);

builder.Services.AddScoped<IOrchestrationRunRepository, OrchestrationRunRepository>();
builder.Services.AddScoped<IOrchestrationProgressReporter, OrchestrationProgressReporter>();
builder.Services.AddScoped<IOrchestrationRunTraceService, OrchestrationRunTraceService>();
builder.Services.AddScoped<IAutonomousCodingOrchestrator, AutonomousCodingOrchestrator>();
builder.Services.AddScoped<OrchestrationRunExecutionEngine>();

        builder.Services.AddHostedService<WorkflowRecoveryHostedService>();
        builder.Services.AddHostedService<OrchestrationRunWorker>();
        builder.Services.AddHostedService<AgentRunWorker>();
    }
}
