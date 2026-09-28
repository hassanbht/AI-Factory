using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Application.Learning;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Security;
using AI.Factory.Application.Tools;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Gateways;
using AI.Factory.Infrastructure.Agents.OpenHands;
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

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var databaseOptions = host.Services.GetRequiredService<DatabaseOptions>();
SqliteStorageInitializer.EnsureDataDirectoryExists(databaseOptions.ConnectionString, logger);

// Phase 18.29.1 §C — fail closed BEFORE any hosted service starts claiming work.
// A Worker that boots with a model its provider cannot serve would claim AgentRuns
// only to fail every one of them, which is how release blocker 2 stayed invisible.
await ModelRoutingPreflightStartup.RunOrThrowAsync(host.Services);

host.Run();

public partial class Program
{
    // Extracted so a composition-root test can build the exact same service
    // graph and resolve it, without invoking host.Run() (which blocks and
    // starts the hosted services). See AI.Factory.Worker.Tests.
    public static void ConfigureServices(HostApplicationBuilder builder)
    {
        builder.Configuration.AddPlatformSpecificOverlay(builder.Environment);

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

        databaseOptions.ConnectionString = SqliteStorageInitializer.NormalizeConnectionString(
            databaseOptions.ConnectionString, builder.Environment.ContentRootPath);

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

        builder.Services.AddScoped<IWorkItemRepository, WorkItemRepository>();
        builder.Services.AddSingleton<ITaskDependencyResolver, TaskDependencyResolver>();
        builder.Services.AddScoped<ITaskStateManager, TaskStateManager>();

        // Phase 18.3 — Deterministic Task Scheduler & Dispatch Engine.
        var taskSchedulerOptions = new TaskSchedulerOptions();
        builder.Configuration.GetSection(TaskSchedulerOptions.SectionName).Bind(taskSchedulerOptions);
        builder.Services.AddSingleton(taskSchedulerOptions);
        builder.Services.AddSingleton<ISchedulerStatusTracker, SchedulerStatusTracker>();
        builder.Services.AddScoped<IWorkItemExecutionResultMapper, WorkItemExecutionResultMapper>();
        // Phase 18.21 — deterministic, Factory-owned acceptance criteria, evaluated
        // by VerificationGate after (never instead of) the mandatory build/test run.
        builder.Services.AddScoped<IWorkItemAcceptanceVerifier, WorkItemAcceptanceVerifier>();
        builder.Services.AddScoped<IVerificationGate, VerificationGate>();
        builder.Services.AddScoped<IAgentRunWorkItemSync, AgentRunWorkItemSync>();

        // Phase 18.10 — Deterministic Project Completion & Review Gate.
        builder.Services.AddScoped<IProjectCompletionGate, ProjectCompletionGate>();

        // Phase 18.25 — the single Project lifecycle authority (Analyzing -> ... ->
        // Planning -> real Planner -> Implementation). Registered in both composition
        // roots: the Api exposes the start operation, the Worker drives progression.
        builder.Services.AddScoped<IProjectLifecycleOrchestrator, ProjectLifecycleOrchestrator>();
        builder.Services.AddScoped<ITaskDispatchScheduler, TaskDispatchScheduler>();

        // Phase 18.5 — Deterministic Project Planner & Execution Plan
        builder.Services.AddScoped<IProjectPlanService, AI.Factory.Application.Planning.ProjectPlanService>();

        // Phase 18.6 — LLM Planner Agent & Candidate Plan Generation
        var plannerOptions = new AI.Factory.Application.Planning.PlannerOptions();
        builder.Configuration.GetSection(AI.Factory.Application.Planning.PlannerOptions.SectionName).Bind(plannerOptions);
        builder.Services.AddSingleton(plannerOptions);
        builder.Services.AddScoped<AI.Factory.Application.Planning.IPlannerResultMapper, AI.Factory.Application.Planning.PlannerResultMapper>();
        // ProjectPlanner depends on IAgentRunService, which this root registered
        // IProjectPlanner without — so resolving the planner in the Worker process
        // threw "Unable to resolve service for type 'IAgentRunService'". The Api root
        // has always registered it; the Worker's omission only surfaced when the
        // Phase 18.22 real-Planner E2E first tried to resolve the planner here.
        builder.Services.AddScoped<IAgentRunService, AgentRunService>();
        builder.Services.AddScoped<AI.Factory.Application.Planning.IProjectPlanner, AI.Factory.Application.Planning.ProjectPlanner>();

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

        hermesOptions.DataVolume = DeploymentPaths.Expand(hermesOptions.DataVolume);
        hermesOptions.WorkspaceRoot = DeploymentPaths.Expand(hermesOptions.WorkspaceRoot);

        builder.Services.AddSingleton(hermesOptions);

        var commandPolicyOptions = new CommandPolicyOptions();
        builder.Configuration.GetSection("CommandPolicy").Bind(commandPolicyOptions);
        builder.Services.AddSingleton(commandPolicyOptions);
        builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddScoped<HermesAgentExecutor>();
        builder.Services.AddScoped<AI.Factory.Application.Abstractions.Execution.IContainerLifecycleManager, DockerContainerLifecycleManager>();

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

        // Execution-adapter Phases B/C — IAgentRuntime is the Factory dispatcher
        // (AgentDefinition policy, then AgentType -> Execution profile -> adapter).
        // The only adapter is HermesExecutionAdapter, which runs Hermes through the
        // same resilience-decorated IAgentExecutor above (used by ModelTaskPlanner
        // Execution-adapter Phases B/C — IAgentRuntime is the Factory dispatcher
        // (AgentDefinition policy, then AgentType -> Execution profile -> adapter).
        // The only adapter is HermesExecutionAdapter, which runs Hermes through the
        // same resilience-decorated IAgentExecutor above (used by ModelTaskPlanner
        // and HermesExecutionAdapter; Review migrated to execution adapter in Phase H).
        // No "Execution" section = the built-in single Hermes profile.
        var executionOptions = new ExecutionOptions();
        builder.Configuration.GetSection(ExecutionOptions.SectionName).Bind(executionOptions);
        executionOptions.ApplyBuiltInDefault(HermesExecutionAdapter.AdapterKey);
        executionOptions.Validate();
        builder.Services.AddSingleton(executionOptions);

        var modelGatewayOptions = new ModelGatewayOptions();
        builder.Configuration.GetSection(ModelGatewayOptions.SectionName).Bind(modelGatewayOptions);
        if (modelGatewayOptions.Gateways.Count == 0 && aiProviderOptions.Providers.Count > 0)
        {
            foreach (var (providerId, providerSettings) in aiProviderOptions.Providers)
            {
                modelGatewayOptions.Gateways[providerId] = new GatewayEntryOptions
                {
                    Enabled = providerSettings.Enabled,
                    BaseUrl = providerSettings.BaseUrl ?? string.Empty,
                    ApiKeySecretName = providerSettings.ApiKeySecretName,
                    TimeoutSeconds = providerSettings.TimeoutSeconds
                };
            }
        }
        modelGatewayOptions.Validate();
        builder.Services.AddSingleton(modelGatewayOptions);

        builder.Services.AddScoped<OpenRouterGateway>(sp =>
        {
            var options = sp.GetRequiredService<ModelGatewayOptions>();
            options.Gateways.TryGetValue(OpenRouterGateway.Key, out var entry);
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenRouterGateway.Key);
            return new OpenRouterGateway(
                httpClient,
                sp.GetRequiredService<ISecretProvider>(),
                sp.GetRequiredService<ILogger<OpenRouterGateway>>(),
                entry?.BaseUrl,
                entry?.ApiKeySecretName);
        });

        builder.Services.AddScoped<OmniRouteGateway>(sp =>
        {
            var options = sp.GetRequiredService<ModelGatewayOptions>();
            options.Gateways.TryGetValue(OmniRouteGateway.Key, out var entry);
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(OmniRouteGateway.Key);
            return new OmniRouteGateway(
                httpClient,
                sp.GetRequiredService<ISecretProvider>(),
                sp.GetRequiredService<ILogger<OmniRouteGateway>>(),
                entry?.BaseUrl,
                entry?.ApiKeySecretName);
        });

        builder.Services.AddScoped<IModelGatewayRegistry>(sp =>
        {
            var registry = new ModelGatewayRegistry();
            registry.Register(sp.GetRequiredService<OpenRouterGateway>());
            registry.Register(sp.GetRequiredService<OmniRouteGateway>());
            return registry;
        });

        builder.Services.AddScoped<ModelGatewayResolver>();

        builder.Services.AddScoped<IExecutionAdapter, HermesExecutionAdapter>();

        var openHandsOptions = new OpenHandsOptions();
        builder.Configuration.GetSection(OpenHandsOptions.SectionName).Bind(openHandsOptions);
        builder.Services.AddSingleton(openHandsOptions);
        builder.Services.AddScoped<IExecutionAdapter, OpenHandsExecutionAdapter>();

        builder.Services.AddScoped<IExecutionAdapterRegistry, ExecutionAdapterRegistry>();
        builder.Services.AddScoped<ExecutionProfileResolver>();
        builder.Services.AddScoped<AdapterDispatchingAgentRuntime>();
        builder.Services.AddScoped<IAgentRuntime>(sp => sp.GetRequiredService<AdapterDispatchingAgentRuntime>());
        builder.Services.AddScoped<IAgentExecutionDispatcher>(sp => sp.GetRequiredService<AdapterDispatchingAgentRuntime>());

        var modelRoutingOptions = new ModelRoutingOptions();
        builder.Configuration.GetSection("ModelRouting").Bind(modelRoutingOptions);
        builder.Services.AddSingleton(modelRoutingOptions);

        // Phase 18.21 §15 — deterministic, offline configuration validation (never a
        // provider call). A placeholder or incoherent catalog is reported loudly
        // rather than only surfacing much later as an unexplained agent failure, as
        // the shipped placeholder ids did for the review stage in Phase 18.20.
        foreach (var problem in modelRoutingOptions.Validate())
        {
            Console.Error.WriteLine($"[ModelRouting] {problem}");
        }
        builder.Services.AddSingleton<IModelRouter, ModelRouter>();

        // Phase 18.29.1 §C — the online half of the same question. Validate() above
        // proves the catalog is internally coherent; this proves the provider can
        // actually serve what it names. "auto/coding:free" passed every structural
        // check while OpenRouter rejected it with HTTP 400, which is how it reached
        // the release gate. Enabled in Production only (see appsettings.Production.json).
        var modelRoutingPreflightOptions = new ModelRoutingPreflightOptions();
builder.Configuration.GetSection(ModelRoutingPreflightOptions.SectionName).Bind(modelRoutingPreflightOptions);
builder.Services.AddModelRoutingPreflight(
    modelRoutingPreflightOptions,
    aiProviderOptions,
    (sp, name) => sp.GetRequiredService<IHttpClientFactory>().CreateClient(name));

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

        builder.Services.AddHttpClient(OpenRouterGateway.Key);
        builder.Services.AddHttpClient(OmniRouteGateway.Key);

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

        builder.Services.AddScoped<IAgentDefinitionRepository, AgentDefinitionRepository>();
        builder.Services.AddScoped<IAgentRunRepository, AgentRunRepository>();
        builder.Services.AddScoped<IArtifactRecorder, FileSystemArtifactRecorder>();

        builder.Services.AddScoped<IBuildExecutor, DotNetBuildExecutor>();
        builder.Services.AddScoped<ITestExecutor, DotNetTestExecutor>();
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

        // Phase 18.14 — Deterministic Agent Workspace & Execution Context. Unset
        // AllowedRoot (default) preserves the prior implicit trust model for
        // Project.RootPath; when configured, ProjectWorkspaceResolver enforces it via
        // the existing WorkspacePathGuard.
        var agentWorkspaceOptions = new AgentWorkspaceOptions();
        builder.Configuration.GetSection("AgentWorkspace").Bind(agentWorkspaceOptions);
        agentWorkspaceOptions.ProjectsBaseDirectory = DeploymentPaths.Expand(agentWorkspaceOptions.ProjectsBaseDirectory);
        agentWorkspaceOptions.AllowedRoot = DeploymentPaths.Expand(agentWorkspaceOptions.AllowedRoot);
        agentWorkspaceOptions.Validate();
        builder.Services.AddSingleton(agentWorkspaceOptions);
        builder.Services.AddScoped<IWorkspaceResolver, ProjectWorkspaceResolver>();
        builder.Services.AddScoped<IProjectWorkspaceProvisioner, ProjectWorkspaceProvisioner>();

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

        builder.Services.AddScoped<IEvaluationEngine, EvaluationEngine>();
        builder.Services.AddScoped<IEvaluationRunRepository, EvaluationRunRepository>();
        builder.Services.AddScoped<ILearningStore, LearningStore>();
        builder.Services.AddScoped<LearningService>();
        builder.Services.AddScoped<IWorkItemCompletionProcessor, WorkItemCompletionProcessor>();

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

        orchestrationOptions.WorkspaceRoot = DeploymentPaths.Expand(orchestrationOptions.WorkspaceRoot);
        orchestrationOptions.Validate();
        builder.Services.AddSingleton(orchestrationOptions);

        builder.Services.AddScoped<IOrchestrationRunRepository, OrchestrationRunRepository>();
        builder.Services.AddScoped<IOrchestrationProgressReporter, OrchestrationProgressReporter>();
        builder.Services.AddScoped<IOrchestrationRunTraceService, OrchestrationRunTraceService>();
        builder.Services.AddScoped<OrchestrationRunExecutionEngine>();

        builder.Services.AddHostedService<WorkflowRecoveryHostedService>();
        builder.Services.AddHostedService<OrchestrationRunWorker>();
        builder.Services.AddHostedService<AgentRunWorker>();
        builder.Services.AddHostedService<TaskSchedulerHostedService>();
    }
}
