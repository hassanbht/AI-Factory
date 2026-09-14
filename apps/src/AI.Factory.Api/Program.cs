using System.Text.Json;
using System.Threading.RateLimiting;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Learning;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Security;
using AI.Factory.Application.Tools;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Configuration;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Providers;
using AI.Factory.Infrastructure.Resilience;
using AI.Factory.Infrastructure.Shutdown;
using AI.Factory.Api.HealthChecks;
using AI.Factory.Api.Middleware;
using AI.Factory.Application.Resilience;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Validation;
using AI.Factory.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------
// Services
// ------------------------------------------------------------

builder.Services.AddOpenApi();

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

var backupOptions = new BackupOptions();
builder.Configuration.GetSection(BackupOptions.SectionName).Bind(backupOptions);
backupOptions.Validate();
builder.Services.AddSingleton(backupOptions);
builder.Services.AddScoped<IDatabaseBackupService, DatabaseBackupService>();

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

var executionLimits = new ExecutionLimitsOptions();
builder.Configuration.GetSection("ExecutionLimits").Bind(executionLimits);
executionLimits.Validate();
builder.Services.AddSingleton(executionLimits);

builder.Services.AddDbContext<FactoryDbContext>(options =>
{
    options.UseSqlite(databaseOptions.ConnectionString, sqliteOptions =>
    {
        sqliteOptions.CommandTimeout((int)databaseResilienceOptions.CommandTimeout.TotalSeconds);
    });
    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<ProjectService>();

builder.Services.AddScoped<IWorkItemRepository, WorkItemRepository>();
builder.Services.AddSingleton<ITaskDependencyResolver, TaskDependencyResolver>();
builder.Services.AddScoped<ITaskStateManager, TaskStateManager>();

// Phase 18.3 — Deterministic Task Scheduler & Dispatch Engine. The scheduling
// loop itself runs in the Worker process (TaskSchedulerHostedService); these
// registrations exist here so /api/scheduler/status has something to read —
// same split-process pattern as IOperationalMetrics/GetSnapshot below.
var taskSchedulerOptions = new TaskSchedulerOptions();
builder.Configuration.GetSection(TaskSchedulerOptions.SectionName).Bind(taskSchedulerOptions);
builder.Services.AddSingleton(taskSchedulerOptions);
builder.Services.AddSingleton<ISchedulerStatusTracker, SchedulerStatusTracker>();

// Phase 18.2 — Project & Task Domain Foundation
builder.Services.AddScoped<AI.Factory.Application.Tasks.WorkItemService>();
builder.Services.AddScoped<AI.Factory.Application.Tasks.TaskDependencyService>();
builder.Services.AddScoped<AI.Factory.Application.Tasks.TaskReadinessService>();

// Phase 18.5 — Deterministic Project Planner & Execution Plan
builder.Services.AddScoped<IProjectPlanService, AI.Factory.Application.Planning.ProjectPlanService>();

// Phase 18.6 — LLM Planner Agent & Candidate Plan Generation
var plannerOptions = new AI.Factory.Application.Planning.PlannerOptions();
builder.Configuration.GetSection(AI.Factory.Application.Planning.PlannerOptions.SectionName).Bind(plannerOptions);
builder.Services.AddSingleton(plannerOptions);
builder.Services.AddScoped<AI.Factory.Application.Planning.IPlannerResultMapper, AI.Factory.Application.Planning.PlannerResultMapper>();
builder.Services.AddScoped<AI.Factory.Application.Planning.IProjectPlanner, AI.Factory.Application.Planning.ProjectPlanner>();

var hermesOptions = new HermesOptions();
builder.Configuration.GetSection("Hermes").Bind(hermesOptions);

if (string.IsNullOrWhiteSpace(hermesOptions.DataVolume))
{
    hermesOptions.DataVolume = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".hermes");
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

builder.Services.AddScoped<IAgentDefinitionRepository, AgentDefinitionRepository>();
builder.Services.AddScoped<IAgentRunRepository, AgentRunRepository>();
builder.Services.AddScoped<IArtifactRecorder, FileSystemArtifactRecorder>();

builder.Services.AddScoped<IBuildExecutor, DotNetBuildExecutor>();
builder.Services.AddScoped<ITestExecutor, DotNetTestExecutor>();
builder.Services.AddScoped<IValidationRunRepository, ValidationRunRepository>();

// Phase 18.13 — IVerificationGate/IProjectCompletionGate/ITaskDispatchScheduler
// are registered here too (not just in the Worker) so CriticalServicesHealthCheck
// can resolve the new authoritative dispatch service; the Worker process still
// owns the actual scheduling loop (TaskSchedulerHostedService).
builder.Services.AddScoped<IVerificationGate, VerificationGate>();
builder.Services.AddScoped<IProjectCompletionGate, ProjectCompletionGate>();
builder.Services.AddScoped<ITaskDispatchScheduler, TaskDispatchScheduler>();

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
builder.Services.AddSingleton(agentWorkspaceOptions);
builder.Services.AddScoped<IWorkspaceResolver, ProjectWorkspaceResolver>();

// Phase 18.1.5 — Agent Orchestration Service. The Worker hosts the actual
// AgentRunWorker background loop; the API only needs IAgentOrchestrator
// registered for future direct-invocation endpoints (out of scope here).
builder.Services.AddScoped<IAgentInstructionBuilder, AgentInstructionBuilder>();
builder.Services.AddSingleton<IStructuredAgentResultParser, StructuredAgentResultParser>();
builder.Services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();

// Phase 18.1.6 — Agent API. Application-layer use-cases behind /api/agents/...
builder.Services.AddScoped<IAgentDefinitionService, AgentDefinitionService>();
builder.Services.AddScoped<IAgentRunService, AgentRunService>();

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

// ------------------------------------------------------------
// Observability (Phase 17.3) — operation correlation, metrics,
// failure classification, diagnostics. All in-process, no external
// infrastructure.
// ------------------------------------------------------------

builder.Services.AddSingleton<OperationContextAccessor>();
builder.Services.AddSingleton<IOperationContextAccessor>(sp => sp.GetRequiredService<OperationContextAccessor>());
builder.Services.AddSingleton<IOperationalMetrics, OperationalMetrics>();
builder.Services.AddSingleton<IBackgroundOperationTracker, BackgroundOperationTracker>();
builder.Services.AddSingleton<IApplicationShutdown, ApplicationShutdownCoordinator>();
builder.Services.AddSingleton<IOperationScopeFactory, OperationScopeFactory>();
builder.Services.AddSingleton<IFailureClassifier, FailureClassifier>();
builder.Services.AddSingleton<ApiExceptionMapper>();
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
builder.Services.AddScoped<IOrchestrationService, OrchestrationService>();

// ------------------------------------------------------------
// Built-in Rate Limiting (Phase 17.4.5)
// ------------------------------------------------------------

builder.Services.AddRateLimiter(limiterOptions =>
{
    limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiterOptions.OnRejected = async (context, cancellationToken) =>
    {
        var correlationId = context.HttpContext.Items.TryGetValue(CorrelationMiddleware.ItemKey, out var itemVal) && itemVal is not null
            ? itemVal.ToString()!
            : context.HttpContext.TraceIdentifier;

        if (!context.HttpContext.Response.Headers.ContainsKey(CorrelationMiddleware.HeaderName))
        {
            context.HttpContext.Response.Headers[CorrelationMiddleware.HeaderName] = correlationId;
        }

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
        }
        else
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
        }

        var metrics = context.HttpContext.RequestServices.GetService<IOperationalMetrics>();
        metrics?.RecordRateLimitRejected();

        var auditLogger = context.HttpContext.RequestServices.GetService<IAuditLogger>();
        if (auditLogger is not null)
        {
            var executionId = Guid.TryParse(correlationId, out var parsed) ? parsed : Guid.NewGuid();
            try
            {
                await auditLogger.LogAsync(
                    executionId: executionId,
                    eventType: AuditEventType.RateLimitRejected,
                    severity: AuditSeverity.Warning,
                    message: $"Rate limit exceeded for {context.HttpContext.Request.Method} {context.HttpContext.Request.Path}",
                    correlationId: executionId,
                    outcome: "Rejected",
                    actorType: "Anonymous",
                    cancellationToken: cancellationToken);
            }
            catch
            {
                // Observability failure must not crash rate limit handling
            }
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/problem+json";

        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Type = "https://httpstatuses.com/429",
            Title = "Too many requests",
            Status = StatusCodes.Status429TooManyRequests,
            Detail = "Quota exceeded. Please retry after some time.",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["code"] = "too_many_requests";
        problem.Extensions["traceId"] = correlationId;

        await JsonSerializer.SerializeAsync(
            context.HttpContext.Response.Body,
            problem,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
            cancellationToken);
    };

    limiterOptions.AddPolicy<string>("GeneralApi", httpContext =>
    {
        var options = httpContext.RequestServices.GetRequiredService<SecurityOptions>();
        if (!options.RateLimiting.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        return RateLimitPartition.GetFixedWindowLimiter("GeneralApi", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.RateLimiting.General.PermitLimit,
            Window = TimeSpan.FromSeconds(options.RateLimiting.General.WindowSeconds),
            QueueLimit = options.RateLimiting.General.QueueLimit
        });
    });

    limiterOptions.AddPolicy<string>("OperationalApi", httpContext =>
    {
        var options = httpContext.RequestServices.GetRequiredService<SecurityOptions>();
        if (!options.RateLimiting.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        return RateLimitPartition.GetFixedWindowLimiter("OperationalApi", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.RateLimiting.Operational.PermitLimit,
            Window = TimeSpan.FromSeconds(options.RateLimiting.Operational.WindowSeconds),
            QueueLimit = options.RateLimiting.Operational.QueueLimit
        });
    });

    limiterOptions.AddPolicy<string>("ExpensiveOperation", httpContext =>
    {
        var options = httpContext.RequestServices.GetRequiredService<SecurityOptions>();
        if (!options.RateLimiting.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        return RateLimitPartition.GetFixedWindowLimiter("ExpensiveOperation", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.RateLimiting.Expensive.PermitLimit,
            Window = TimeSpan.FromSeconds(options.RateLimiting.Expensive.WindowSeconds),
            QueueLimit = options.RateLimiting.Expensive.QueueLimit
        });
    });
});

// ------------------------------------------------------------
// CORS (Phase 17.4.5)
// ------------------------------------------------------------

if (securityOptions.Cors.Enabled)
{
    builder.Services.AddCors(corsOptions =>
    {
        corsOptions.AddPolicy("ConfiguredCorsPolicy", policy =>
        {
            if (securityOptions.Cors.AllowedOrigins is { Length: > 0 })
            {
                policy.WithOrigins(securityOptions.Cors.AllowedOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            }
        });
    });
}

// ------------------------------------------------------------
// Forwarded Headers (Phase 17.4.5)
// ------------------------------------------------------------

if (securityOptions.ForwardedHeaders.Enabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                                   Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    });
}

// ------------------------------------------------------------
// Server Header & Request Limits
// ------------------------------------------------------------

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = securityOptions.RequestLimits.MaxRequestBodySizeBytes;
});

// ------------------------------------------------------------
// Health checks — kept isolated from business logic (Api/HealthChecks/
// only, no calls into orchestration services). "ready" tag = readiness;
// no tag at all = liveness runs zero checks (process responding is enough).
// ------------------------------------------------------------

var healthChecksBuilder = builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" })
    .AddCheck<ConfigurationHealthCheck>("configuration", tags: new[] { "ready" })
    .AddCheck<CriticalServicesHealthCheck>("critical-services", tags: new[] { "ready" })
    .AddCheck<CircuitBreakerHealthCheck>("circuit-breaker", tags: new[] { "ready" })
    .AddCheck<ShutdownHealthCheck>("shutdown", tags: new[] { "ready" });

if (builder.Configuration.GetValue<bool>("Health:CheckOpenViking"))
{
    healthChecksBuilder.AddCheck<OpenVikingHealthCheck>("openviking", tags: new[] { "ready" });
}

var app = builder.Build();

if (args.Contains("--migrate-only"))
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Dedicated migration runner started.");

    if (args.Contains("--simulate-failure") ||
        string.Equals(Environment.GetEnvironmentVariable("AI_FACTORY_MIGRATE_FAIL_TEST"), "1", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogError("Simulated migration failure triggered for verification. Exiting with code 1.");
        Console.Error.WriteLine("Simulated migration failure triggered for verification. Exiting with code 1.");
        Console.Error.Flush();
        Environment.ExitCode = 1;
        await ((IAsyncDisposable)app).DisposeAsync();
        return;
    }

    var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
    logger.LogInformation("Applying pending database migrations...");
    try
    {
        await db.Database.MigrateAsync();
        logger.LogInformation("Database migrations successfully applied. Exiting with code 0.");
        Environment.ExitCode = 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database migration failed. Exiting with code 1.");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--backup-database"))
{
    using var scope = app.Services.CreateScope();
    var backupService = scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Dedicated backup runner started.");
    try
    {
        var result = await backupService.CreateBackupAsync();
        logger.LogInformation(
            "Backup completed: {FileName} ({SizeBytes} bytes, validated={Validated}). Exiting with code 0.",
            result.FileName, result.SizeBytes, result.ValidationPassed);
        Environment.ExitCode = 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Backup failed. Exiting with code 1.");
        Environment.ExitCode = 1;
    }

    return;
}

var restoreArgIndex = Array.IndexOf(args, "--restore-database");
if (restoreArgIndex >= 0)
{
    using var scope = app.Services.CreateScope();
    var backupService = scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    if (restoreArgIndex + 1 >= args.Length)
    {
        logger.LogError("--restore-database requires a backup file path argument. Exiting with code 2.");
        Environment.ExitCode = 2;
        return;
    }

    var backupFilePath = args[restoreArgIndex + 1];
    logger.LogWarning("Dedicated restore runner started for backup file {BackupFileName}. This will replace the active database.", Path.GetFileName(backupFilePath));
    try
    {
        var result = await backupService.RestoreAsync(backupFilePath);
        logger.LogInformation(
            "Restore completed from {RestoredFromFileName} (pre-restore safety backup: {SafetyBackupFileName}). Exiting with code 0.",
            result.RestoredFromFileName, result.PreRestoreSafetyBackupFileName ?? "none");
        Environment.ExitCode = 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Restore failed; the active database was not modified. Exiting with code 1.");
        Environment.ExitCode = 1;
    }

    return;
}

// ------------------------------------------------------------
// Middleware Pipeline (Section 18 Ordering)
// ------------------------------------------------------------

app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();

if (securityOptions.ForwardedHeaders.Enabled)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment() && securityOptions.Https.EnableHstsInProduction)
{
    app.UseHsts();
}

if (securityOptions.Https.RedirectHttpToHttps)
{
    app.UseHttpsRedirection();
}

if (securityOptions.Cors.Enabled)
{
    app.UseCors("ConfiguredCorsPolicy");
}

app.UseMiddleware<RequestSizeLimitMiddleware>();
app.UseMiddleware<ShutdownMiddleware>();

app.UseRateLimiter();

app.UseMiddleware<OperationalEndpointSecurityMiddleware>();

// ------------------------------------------------------------
// Startup recovery — resumability after a process restart
// ------------------------------------------------------------

using (var startupScope = app.Services.CreateScope())
{
    var taskStateManager = startupScope.ServiceProvider.GetRequiredService<ITaskStateManager>();
    await taskStateManager.RecoverOrphanedTasksAsync();
}

// ------------------------------------------------------------
// HTTP Pipeline
// ------------------------------------------------------------

var shouldExposeOpenApi = securityOptions.OpenApi.Enabled ?? app.Environment.IsDevelopment();
if (shouldExposeOpenApi)
{
    app.MapOpenApi();
}

// ------------------------------------------------------------
// Health
// ------------------------------------------------------------

// Liveness: only "is the process alive and able to respond" — never
// touches the database, config validation, or DI resolution.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});

// Readiness: dependencies the app actually needs to serve traffic.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

// Combined view — same checks as readiness, at the conventional path.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

// ------------------------------------------------------------
// Projects
// ------------------------------------------------------------

app.MapPost("/api/projects", async (
    CreateProjectRequest request,
    ProjectService service,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        throw new ValidationException("Project name is required.");
    }

    if (request.Name.Length > 200)
    {
        throw new ValidationException("Project name cannot exceed 200 characters.");
    }

    if (request.Name.Contains("..") || request.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
    {
        throw new ValidationException("Project name contains invalid characters.");
    }

    if (request.Description?.Length > 2000)
    {
        throw new ValidationException("Project description cannot exceed 2000 characters.");
    }

    var project = await service.CreateAsync(
        request.Name,
        request.Description ?? string.Empty,
        cancellationToken);

    return Results.Created(
        $"/api/projects/{project.Id}",
        ToProjectDto(project));
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/projects", async (
    ProjectService service,
    CancellationToken cancellationToken) =>
{
    var projects = await service.ListAsync(cancellationToken);
    return Results.Ok(projects.Select(ToProjectDto));
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/projects/{id}", async (
    string id,
    IProjectRepository repository,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedGuid) || parsedGuid == Guid.Empty)
    {
        throw new ValidationException("Invalid project identifier.");
    }

    var project = await repository.GetByIdAsync(
        parsedGuid,
        cancellationToken);

    return project is null
        ? Results.NotFound()
        : Results.Ok(ToProjectDto(project));
}).RequireRateLimiting("GeneralApi");

app.MapPatch("/api/projects/{id}/status", async (
    string id,
    UpdateStatusRequest request,
    ProjectService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedGuid) || parsedGuid == Guid.Empty)
    {
        throw new ValidationException("Invalid project identifier.");
    }

    if (!Enum.TryParse<ProjectStatus>(request.Status, ignoreCase: true, out var next) || !Enum.IsDefined(next))
    {
        throw new ValidationException($"Invalid project status '{request.Status}'.");
    }

    var project = await service.TransitionStatusAsync(parsedGuid, next, cancellationToken);
    return Results.Ok(ToProjectDto(project));
}).RequireRateLimiting("GeneralApi");

// Phase 18.11 — the sole explicit human-approval boundary: HumanReview ->
// Completed. Deliberately separate from the generic status endpoint above,
// which can no longer produce Completed under any input.
app.MapPost("/api/projects/{id}/approve", async (
    string id,
    ApproveProjectRequest? request,
    ProjectService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedGuid) || parsedGuid == Guid.Empty)
    {
        throw new ValidationException("Invalid project identifier.");
    }

    var project = await service.ApproveAsync(parsedGuid, request?.Actor, cancellationToken);
    return Results.Ok(ToProjectDto(project));
}).RequireRateLimiting("GeneralApi");

// ------------------------------------------------------------
// Tasks (Phase 18.2)
// ------------------------------------------------------------

app.MapPost("/api/projects/{projectId}/tasks", async (
    string projectId,
    CreateWorkItemRequest request,
    AI.Factory.Application.Tasks.WorkItemService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(projectId, out var parsedProjectId) || parsedProjectId == Guid.Empty)
    {
        throw new ValidationException("Invalid project identifier.");
    }

    if (!Enum.TryParse<WorkItemType>(request.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type))
    {
        throw new ValidationException($"Invalid task type '{request.Type}'.");
    }

    var workItem = await service.CreateAsync(
        parsedProjectId, type, request.Title, request.Description, request.Priority ?? "Normal", cancellationToken);

    return Results.Created($"/api/tasks/{workItem.Id}", ToWorkItemDto(workItem));
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/projects/{projectId}/tasks", async (
    string projectId,
    AI.Factory.Application.Tasks.WorkItemService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(projectId, out var parsedProjectId) || parsedProjectId == Guid.Empty)
    {
        throw new ValidationException("Invalid project identifier.");
    }

    var items = await service.ListByProjectAsync(parsedProjectId, cancellationToken);
    return Results.Ok(items.Select(ToWorkItemDto));
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/tasks/{id}", async (
    string id,
    AI.Factory.Application.Tasks.WorkItemService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid task identifier.");
    }

    var workItem = await service.GetAsync(parsedId, cancellationToken);
    return workItem is null ? Results.NotFound() : Results.Ok(ToWorkItemDto(workItem));
}).RequireRateLimiting("GeneralApi");

app.MapPatch("/api/tasks/{id}/status", async (
    string id,
    UpdateStatusRequest request,
    AI.Factory.Application.Tasks.WorkItemService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid task identifier.");
    }

    if (!Enum.TryParse<WorkItemStatus>(request.Status, ignoreCase: true, out var next) || !Enum.IsDefined(next))
    {
        throw new ValidationException($"Invalid task status '{request.Status}'.");
    }

    var workItem = await service.TransitionStatusAsync(parsedId, next, cancellationToken);
    return Results.Ok(ToWorkItemDto(workItem));
}).RequireRateLimiting("GeneralApi");

app.MapPost("/api/tasks/{id}/dependencies", async (
    string id,
    AddDependencyRequest request,
    AI.Factory.Application.Tasks.TaskDependencyService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid task identifier.");
    }

    if (request.DependsOnTaskId == Guid.Empty)
    {
        throw new ValidationException("DependsOnTaskId must be a non-empty GUID.");
    }

    await service.AddDependencyAsync(parsedId, request.DependsOnTaskId, cancellationToken);
    return Results.Created($"/api/tasks/{id}/dependencies", new { TaskId = parsedId, request.DependsOnTaskId });
}).RequireRateLimiting("GeneralApi");

app.MapDelete("/api/tasks/{id}/dependencies/{dependsOnTaskId}", async (
    string id,
    string dependsOnTaskId,
    AI.Factory.Application.Tasks.TaskDependencyService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty
        || !Guid.TryParse(dependsOnTaskId, out var parsedDependsOnId) || parsedDependsOnId == Guid.Empty)
    {
        throw new ValidationException("Invalid task identifier.");
    }

    await service.RemoveDependencyAsync(parsedId, parsedDependsOnId, cancellationToken);
    return Results.NoContent();
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/tasks/{id}/readiness", async (
    string id,
    AI.Factory.Application.Tasks.TaskReadinessService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid task identifier.");
    }

    var readiness = await service.GetReadinessAsync(parsedId, cancellationToken);
    return Results.Ok(new { TaskId = parsedId, Readiness = readiness.ToString() });
}).RequireRateLimiting("GeneralApi");

// ------------------------------------------------------------
// Task Scheduler (Phase 18.3)
// ------------------------------------------------------------

app.MapGet("/api/scheduler/status", (ISchedulerStatusTracker statusTracker) =>
{
    return Results.Ok(statusTracker.GetSnapshot());
}).RequireRateLimiting("GeneralApi");

// ------------------------------------------------------------
// Project Planner (Phase 18.5)
// ------------------------------------------------------------

app.MapPost("/api/projects/{projectId}/plan", async (
    string projectId,
    CreatePlanRequest request,
    IProjectPlanService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(projectId, out var parsedProjectId) || parsedProjectId == Guid.Empty)
    {
        throw new ValidationException("Invalid project identifier.");
    }

    if (request.Tasks is null || request.Tasks.Count == 0)
    {
        throw new ValidationException("A plan must contain at least one task.");
    }

    var tasks = request.Tasks.Select(t =>
    {
        if (!Enum.TryParse<WorkItemType>(t.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type))
        {
            throw new ValidationException($"Task '{t.PlanKey}' has an invalid type '{t.Type}'.");
        }

        return new PlannedWorkItemInput(
            t.PlanKey ?? string.Empty,
            t.Title ?? string.Empty,
            t.Description,
            type,
            t.Priority ?? "Normal",
            (IReadOnlyList<string>)(t.DependsOn ?? new List<string>()));
    }).ToList();

    var result = await service.CreatePlanAsync(
        new ProjectPlanRequest(parsedProjectId, request.Summary, tasks), cancellationToken);

    return Results.Ok(ToPlanDto(result));
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/projects/{projectId}/plan", async (
    string projectId,
    IProjectPlanService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(projectId, out var parsedProjectId) || parsedProjectId == Guid.Empty)
    {
        throw new ValidationException("Invalid project identifier.");
    }

    var result = await service.GetPlanAsync(parsedProjectId, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(ToPlanDto(result));
}).RequireRateLimiting("GeneralApi");

// Phase 18.6 — initiates Planner Agent execution through the existing
// AgentRun pipeline; never executes Hermes inside this HTTP request. The
// resulting plan is later read back through GET /api/projects/{projectId}/plan
// above (unchanged from Phase 18.5) once AgentRunWorker materializes it.
app.MapPost("/api/projects/{projectId}/plan/generate", async (
    string projectId,
    AI.Factory.Application.Planning.IProjectPlanner planner,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(projectId, out var parsedProjectId) || parsedProjectId == Guid.Empty)
    {
        throw new ValidationException("Invalid project identifier.");
    }

    var run = await planner.GenerateAsync(parsedProjectId, cancellationToken);
    return Results.Created($"/api/agents/runs/{run.Id}", ToAgentRunDto(run));
}).RequireRateLimiting("ExpensiveOperation");

static object ToPlanDto(ProjectPlanResult plan) => new
{
    plan.ProjectId,
    plan.PlanHash,
    Tasks = plan.Tasks.Select(t => new
    {
        t.PlanKey,
        t.WorkItemId,
        t.Title,
        Type = t.Type.ToString(),
        t.Priority,
        t.DependsOn,
        t.Status,
        Readiness = t.Readiness.ToString()
    })
};

static object ToProjectDto(Project project) => new
{
    project.Id,
    project.Name,
    project.Slug,
    project.Description,
    Status = project.Status.ToString(),
    project.CreatedAtUtc,
    project.UpdatedAtUtc
};

static object ToWorkItemDto(WorkItem workItem) => new
{
    workItem.Id,
    workItem.ProjectId,
    Type = workItem.Type.ToString(),
    workItem.Title,
    workItem.Description,
    workItem.Status,
    workItem.Priority,
    workItem.CreatedAtUtc,
    workItem.UpdatedAtUtc
};

// ------------------------------------------------------------
// Human Approval
// ------------------------------------------------------------
// Phase 18.12 — the legacy /api/approvals/* endpoints (backed by
// ApprovalService/IWorkflowEngine, a second independent Project-transition
// authority) were retired. The single authoritative human-approval boundary
// is now POST /api/projects/{id}/approve -> ProjectService.ApproveAsync
// (Phase 18.11), reachable only from HumanReview.

// ------------------------------------------------------------
// Execution Audit Trail
// ------------------------------------------------------------

app.MapGet("/api/audit-log", async (
    string? projectId,
    string? executionId,
    string? severity,
    int? page,
    int? pageSize,
    IAuditLogRepository repository,
    CancellationToken cancellationToken) =>
{
    Guid? parsedProjectId = null;
    if (!string.IsNullOrWhiteSpace(projectId))
    {
        if (!Guid.TryParse(projectId, out var pid) || pid == Guid.Empty)
        {
            throw new ValidationException("Invalid ProjectId identifier.");
        }
        parsedProjectId = pid;
    }

    Guid? parsedExecutionId = null;
    if (!string.IsNullOrWhiteSpace(executionId))
    {
        if (!Guid.TryParse(executionId, out var eid) || eid == Guid.Empty)
        {
            throw new ValidationException("Invalid ExecutionId identifier.");
        }
        parsedExecutionId = eid;
    }

    if (!string.IsNullOrWhiteSpace(severity))
    {
        if (!Enum.TryParse<AuditSeverity>(severity, ignoreCase: true, out var parsedSeverity) || !Enum.IsDefined(typeof(AuditSeverity), parsedSeverity))
        {
            throw new ValidationException($"Invalid severity '{severity}'.");
        }
    }

    if (page.HasValue && page.Value < 1)
    {
        throw new ValidationException("Page must be greater than or equal to 1.");
    }

    if (pageSize.HasValue && pageSize.Value < 1)
    {
        throw new ValidationException("PageSize must be greater than or equal to 1.");
    }

    if (pageSize.HasValue && pageSize.Value > 100)
    {
        throw new ValidationException("PageSize cannot exceed 100.");
    }

    var query = new AuditLogQuery(
        parsedProjectId,
        parsedExecutionId,
        page ?? 1,
        pageSize ?? 50);

    var result = await repository.GetPagedAsync(query, cancellationToken);
    return Results.Ok(result);
}).RequireRateLimiting("OperationalApi");

// ------------------------------------------------------------
// Operational Diagnostics (Phase 17.3)
// ------------------------------------------------------------

app.MapGet("/api/operations/metrics", (IOperationalMetrics metrics) => Results.Ok(metrics.GetSnapshot()))
    .RequireRateLimiting("OperationalApi");

app.MapGet("/api/operations/resilience", (
    ICircuitBreakerRegistry circuitBreakers,
    IBulkheadRegistry bulkheads) =>
{
    var circuitSnapshots = circuitBreakers.GetAllSnapshots().ToDictionary(s => s.DependencyName, StringComparer.OrdinalIgnoreCase);
    var bulkheadSnapshots = bulkheads.GetAllSnapshots().ToDictionary(s => s.DependencyName, StringComparer.OrdinalIgnoreCase);

    var allNames = circuitSnapshots.Keys.Union(bulkheadSnapshots.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();

    var dependencies = allNames.Select(name =>
    {
        circuitSnapshots.TryGetValue(name, out var c);
        bulkheadSnapshots.TryGetValue(name, out var b);

        return new
        {
            name,
            circuitState = c?.State.ToString() ?? "Closed",
            consecutiveFailures = c?.ConsecutiveFailures ?? 0,
            openedAtUtc = c?.OpenedAtUtc,
            nextProbeAtUtc = c?.NextProbeAtUtc,
            bulkhead = b is null ? null : new
            {
                activeExecutions = b.ActiveExecutions,
                queuedExecutions = b.QueuedExecutions,
                maxConcurrentExecutions = b.MaxConcurrentExecutions,
                maxQueueLength = b.MaxQueueLength
            }
        };
    });

    return Results.Ok(new { dependencies });
}).RequireRateLimiting("OperationalApi");

app.MapGet("/api/operations/recent", async (
    int? count,
    IOperationalDiagnosticsService diagnostics,
    CancellationToken cancellationToken) =>
{
    if (count.HasValue && count.Value <= 0)
    {
        throw new ValidationException("Count must be greater than zero.");
    }

    if (count.HasValue && count.Value > 1000)
    {
        throw new ValidationException("Count cannot exceed 1000.");
    }

    var entries = await diagnostics.GetRecentOperationsAsync(count ?? 50, cancellationToken);
    return Results.Ok(entries);
}).RequireRateLimiting("OperationalApi");

app.MapGet("/api/operations/{correlationId}", async (
    string correlationId,
    IOperationalDiagnosticsService diagnostics,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(correlationId, out var parsedCorrelationId) || parsedCorrelationId == Guid.Empty)
    {
        throw new ValidationException("Invalid correlation identifier.");
    }

    var summary = await diagnostics.GetCorrelationSummaryAsync(parsedCorrelationId, cancellationToken);
    return summary is null ? Results.NotFound() : Results.Ok(summary);
}).RequireRateLimiting("OperationalApi");

app.MapGet("/api/projects/{projectId}/diagnostics", async (
    string projectId,
    IOperationalDiagnosticsService diagnostics,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(projectId, out var parsedProjectId) || parsedProjectId == Guid.Empty)
    {
        throw new ValidationException("Invalid project identifier.");
    }

    var summary = await diagnostics.GetProjectSummaryAsync(parsedProjectId, cancellationToken);
    return summary is null ? Results.NotFound() : Results.Ok(summary);
}).RequireRateLimiting("OperationalApi");

app.MapPost("/api/orchestration-runs", async (
    StartOrchestrationRunRequest request,
    IOrchestrationService service,
    CancellationToken cancellationToken) =>
{
    if (request.ProjectId == Guid.Empty)
    {
        throw new ValidationException("ProjectId must be a non-empty GUID.");
    }

    if (request.TaskId == Guid.Empty)
    {
        throw new ValidationException("TaskId must be a non-empty GUID.");
    }

    if (request.MaxAttempts.HasValue && (request.MaxAttempts.Value <= 0 || request.MaxAttempts.Value > 10))
    {
        throw new ValidationException("MaxAttempts must be between 1 and 10.");
    }

    var result = await service.StartAsync(
        new StartOrchestrationCommand(request.ProjectId, request.TaskId, request.MaxAttempts),
        cancellationToken);

    return Results.Accepted(result.StatusUrl, new
    {
        runId = result.RunId,
        status = result.Status,
        statusUrl = result.StatusUrl
    });
}).RequireRateLimiting("ExpensiveOperation");

app.MapGet("/api/orchestration-runs/{id}", async (
    string id,
    IOrchestrationService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid run identifier.");
    }

    var run = await service.GetAsync(parsedId, cancellationToken);

    if (run is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        run.Id,
        run.ProjectId,
        run.WorkItemId,
        Status = run.Status.ToString(),
        CurrentStage = run.CurrentStage.ToString(),
        run.Attempt,
        run.MaxAttempts,
        run.SelectedModel,
        run.RequestedAtUtc,
        run.StartedAtUtc,
        run.CompletedAtUtc,
        FailureCategory = run.FailureCategory?.ToString(),
        run.ErrorMessage,
        Trace = run.Steps
            .OrderBy(s => s.Sequence)
            .Select(s => new
            {
                Stage = s.Stage.ToString(),
                Status = s.Status.ToString(),
                s.AttemptNumber,
                s.StartedAtUtc,
                s.CompletedAtUtc,
                s.FailureType,
                s.FailureMessage,
                Metadata = s.MetadataJson is null
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(s.MetadataJson)
            })
    });
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/orchestration-runs/{id}/attempts", async (
    string id,
    IOrchestrationService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid run identifier.");
    }

    var run = await service.GetAsync(parsedId, cancellationToken);

    return run is null
        ? Results.NotFound()
        : Results.Ok(run.Attempts.OrderBy(a => a.AttemptNumber));
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/orchestration-runs/{id}/logs", async (
    string id,
    IOrchestrationService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid run identifier.");
    }

    var run = await service.GetAsync(parsedId, cancellationToken);

    return run is null
        ? Results.NotFound()
        : Results.Ok(run.ProgressEvents.OrderBy(e => e.TimestampUtc));
}).RequireRateLimiting("GeneralApi");

app.MapPost("/api/orchestration-runs/{id}/cancel", async (
    string id,
    IOrchestrationService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid run identifier.");
    }

    await service.RequestCancellationAsync(parsedId, cancellationToken);
    return Results.Accepted();
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/operations/active", (
    IApplicationShutdown shutdown,
    IBackgroundOperationTracker tracker) =>
{
    var activeOps = tracker.GetActiveOperations().Select(op => new
    {
        op.OperationId,
        op.CorrelationId,
        op.OperationName,
        op.ProjectId,
        op.TaskId,
        op.RunId,
        op.StartedAtUtc,
        DurationMs = (long)(DateTime.UtcNow - op.StartedAtUtc).TotalMilliseconds
    });

    return Results.Ok(new
    {
        isStopping = shutdown.IsStopping,
        activeCount = tracker.ActiveCount,
        operations = activeOps
    });
}).RequireRateLimiting("OperationalApi");

app.MapGet("/api/operations/configuration", (IConfigurationDiagnosticsService diagnostics) =>
{
    return Results.Ok(diagnostics.GetSnapshot());
}).RequireRateLimiting("OperationalApi");

app.MapGet("/api/operations/database", async (
    IDatabaseDiagnosticsService diagnostics,
    CancellationToken cancellationToken) =>
{
    var snapshot = await diagnostics.GetSnapshotAsync(cancellationToken);
    return Results.Ok(snapshot);
}).RequireRateLimiting("OperationalApi");

app.MapGet("/api/operations/deployment", (IDeploymentDiagnosticsService diagnostics) =>
{
    return Results.Ok(diagnostics.GetSnapshot());
}).RequireRateLimiting("OperationalApi");

// ------------------------------------------------------------
// Agent Definitions (Phase 18.1.6)
// ------------------------------------------------------------

// DTO projections — never return the EF entity directly. AgentRun has no
// persisted ToolCallNames/QueuedAtUtc columns (HermesAgentRuntime doesn't
// call tools yet; CreateAsync queues synchronously so CreatedAtUtc already
// is the queued time) — omitted rather than fabricated, see phase 18.1.6 doc.
static object ToAgentDefinitionDto(AgentDefinition definition) => new
{
    definition.Id,
    definition.Name,
    definition.Description,
    definition.AgentType,
    definition.Enabled,
    definition.CreatedAtUtc,
    definition.UpdatedAtUtc
};

static object ToAgentRunDto(AgentRun run) => new
{
    run.Id,
    run.ProjectId,
    run.WorkItemId,
    run.AgentDefinitionId,
    Status = run.Status.ToString(),
    run.CreatedAtUtc,
    run.StartedAtUtc,
    run.CompletedAtUtc,
    DurationMs = run.StartedAtUtc.HasValue && run.CompletedAtUtc.HasValue
        ? (long?)(run.CompletedAtUtc.Value - run.StartedAtUtc.Value).TotalMilliseconds
        : null,
    run.Output,
    FailureCategory = run.FailureCategory?.ToString(),
    run.ErrorMessage,
    run.CorrelationId,
    StructuredResult = ToStructuredResultDto(run.StructuredResult)
};

// Phase 18.1.8 §9 — kept separate from the persisted JSON blob (run.StructuredResult
// is never exposed raw): parses it back into the same contract shape API consumers
// already understand, and degrades to null rather than surfacing a deserialization
// error for a row written before this contract existed or an already-invalid result.
static object? ToStructuredResultDto(string? structuredResultJson)
{
    if (string.IsNullOrWhiteSpace(structuredResultJson))
    {
        return null;
    }

    try
    {
        var parsed = JsonSerializer.Deserialize<StructuredAgentResult>(structuredResultJson, StructuredAgentResult.JsonOptions);

        return parsed is null
            ? null
            : new
            {
                parsed.Version,
                Status = parsed.Status.ToString(),
                parsed.Summary,
                parsed.Output,
                parsed.Data,
                parsed.Warnings,
                parsed.Errors,
                parsed.Artifacts,
                parsed.Metadata
            };
    }
    catch (JsonException)
    {
        return null;
    }
}

app.MapPost("/api/agents/definitions", async (
    CreateAgentDefinitionRequest request,
    IAgentDefinitionService service,
    CancellationToken cancellationToken) =>
{
    var definition = await service.CreateAsync(
        new CreateAgentDefinitionCommand(
            request.Name, request.Description, request.AgentType, request.SystemPrompt, request.Enabled ?? true),
        cancellationToken);

    return Results.Created($"/api/agents/definitions/{definition.Id}", ToAgentDefinitionDto(definition));
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/agents/definitions/{id}", async (
    string id,
    IAgentDefinitionService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid AgentDefinition identifier.");
    }

    var definition = await service.GetAsync(parsedId, cancellationToken);
    return definition is null ? Results.NotFound() : Results.Ok(ToAgentDefinitionDto(definition));
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/agents/definitions", async (
    IAgentDefinitionService service,
    CancellationToken cancellationToken) =>
{
    var definitions = await service.ListAsync(cancellationToken);
    return Results.Ok(definitions.Select(ToAgentDefinitionDto));
}).RequireRateLimiting("GeneralApi");

// ------------------------------------------------------------
// Agent Runs (Phase 18.1.6)
// ------------------------------------------------------------

app.MapPost("/api/agents/runs", async (
    CreateAgentRunRequest request,
    IAgentRunService service,
    CancellationToken cancellationToken) =>
{
    if (request.AgentDefinitionId == Guid.Empty)
    {
        throw new ValidationException("AgentDefinitionId must be a non-empty GUID.");
    }

    var run = await service.CreateAsync(
        new CreateAgentRunCommand(request.AgentDefinitionId, request.ProjectId, request.Input),
        cancellationToken);

    return Results.Created($"/api/agents/runs/{run.Id}", ToAgentRunDto(run));
}).RequireRateLimiting("ExpensiveOperation");

app.MapGet("/api/agents/runs/{id}", async (
    string id,
    IAgentRunService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid AgentRun identifier.");
    }

    var run = await service.GetAsync(parsedId, cancellationToken);
    return run is null ? Results.NotFound() : Results.Ok(ToAgentRunDto(run));
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/agents/runs", async (
    string? projectId,
    string? agentDefinitionId,
    string? status,
    DateTime? from,
    DateTime? to,
    int? page,
    int? pageSize,
    IAgentRunService service,
    CancellationToken cancellationToken) =>
{
    Guid? parsedProjectId = null;
    if (!string.IsNullOrWhiteSpace(projectId))
    {
        if (!Guid.TryParse(projectId, out var pid) || pid == Guid.Empty)
        {
            throw new ValidationException("Invalid ProjectId identifier.");
        }
        parsedProjectId = pid;
    }

    Guid? parsedDefinitionId = null;
    if (!string.IsNullOrWhiteSpace(agentDefinitionId))
    {
        if (!Guid.TryParse(agentDefinitionId, out var did) || did == Guid.Empty)
        {
            throw new ValidationException("Invalid AgentDefinitionId identifier.");
        }
        parsedDefinitionId = did;
    }

    AgentRunStatus? parsedStatus = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<AgentRunStatus>(status, ignoreCase: true, out var s) || !Enum.IsDefined(typeof(AgentRunStatus), s))
        {
            throw new ValidationException($"Invalid status '{status}'.");
        }
        parsedStatus = s;
    }

    if (page.HasValue && page.Value < 1)
    {
        throw new ValidationException("Page must be greater than or equal to 1.");
    }

    if (pageSize.HasValue && pageSize.Value < 1)
    {
        throw new ValidationException("PageSize must be greater than or equal to 1.");
    }

    if (pageSize.HasValue && pageSize.Value > 100)
    {
        throw new ValidationException("PageSize cannot exceed 100.");
    }

    var query = new AgentRunQuery(
        parsedProjectId, parsedDefinitionId, parsedStatus, from, to, page ?? 1, pageSize ?? 50);

    var result = await service.ListAsync(query, cancellationToken);

    return Results.Ok(new
    {
        Items = result.Items.Select(ToAgentRunDto),
        result.TotalCount,
        result.Page,
        result.PageSize
    });
}).RequireRateLimiting("GeneralApi");

app.MapPost("/api/agents/runs/{id}/cancel", async (
    string id,
    IAgentRunService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid AgentRun identifier.");
    }

    await service.CancelAsync(parsedId, cancellationToken);
    return Results.Accepted();
}).RequireRateLimiting("GeneralApi");

app.MapGet("/api/agents/metrics", async (
    IOperationalMetrics metrics,
    IAgentRunRepository runRepository,
    CancellationToken cancellationToken) =>
{
    var snapshot = metrics.GetSnapshot();
    var queued = await runRepository.CountByStatusAsync(AgentRunStatus.Queued, cancellationToken);
    var running = await runRepository.CountByStatusAsync(AgentRunStatus.Running, cancellationToken);

    return Results.Ok(new
    {
        Queued = queued,
        Running = running,
        Created = snapshot.AgentRunsCreated,
        Started = snapshot.AgentRunsStarted,
        Succeeded = snapshot.AgentRunsSucceeded,
        Failed = snapshot.AgentRunsFailed,
        TimedOut = snapshot.AgentRunsTimedOut,
        Cancelled = snapshot.AgentRunsCancelled,
        WorkerClaimSuccess = snapshot.AgentWorkerClaimSuccess,
        AverageDurationMs = snapshot.AverageAgentExecutionDurationMs,
        TotalDurationMs = snapshot.TotalAgentExecutionDurationMs
    });
}).RequireRateLimiting("OperationalApi");

app.Run();

public sealed record CreateProjectRequest(
    string Name,
    string? Description);

public sealed record UpdateStatusRequest(string Status);

/// <summary>
/// Phase 18.11 — Actor is the simplest repository-compatible identity
/// available: this repo has no real authentication/authorization
/// abstraction, so it is an unauthenticated, caller-supplied label recorded
/// purely for audit purposes, not a verified identity claim.
/// </summary>
public sealed record ApproveProjectRequest(string? Actor);

public sealed record CreateWorkItemRequest(
    string Type,
    string Title,
    string? Description,
    string? Priority);

public sealed record AddDependencyRequest(Guid DependsOnTaskId);

public sealed record PlanTaskRequestDto(
    string PlanKey,
    string Title,
    string? Description,
    string Type,
    string? Priority,
    List<string>? DependsOn);

public sealed record CreatePlanRequest(
    string? Summary,
    List<PlanTaskRequestDto> Tasks);

public sealed record StartOrchestrationRunRequest(
    Guid ProjectId,
    Guid TaskId,
    int? MaxAttempts);

public sealed record CreateAgentDefinitionRequest(
    string Name,
    string? Description,
    string AgentType,
    string? SystemPrompt,
    bool? Enabled);

public sealed record CreateAgentRunRequest(
    Guid AgentDefinitionId,
    Guid? ProjectId,
    string? Input);

// Exposes the top-level-statement entry point to WebApplicationFactory<Program> for integration tests.
public partial class Program
{
}
