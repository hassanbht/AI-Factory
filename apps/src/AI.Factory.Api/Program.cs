using System.Text.Json;
using System.Threading.RateLimiting;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Approvals;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Learning;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Security;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Configuration;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Resilience;
using AI.Factory.Infrastructure.Shutdown;
using AI.Factory.Api.HealthChecks;
using AI.Factory.Api.Middleware;
using AI.Factory.Application.Resilience;
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

var modelRoutingOptions = new ModelRoutingOptions();
builder.Configuration.GetSection("ModelRouting").Bind(modelRoutingOptions);
builder.Services.AddSingleton(modelRoutingOptions);
builder.Services.AddSingleton<IModelRouter, ModelRouter>();

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

builder.Services.AddScoped<IAutonomousCodingOrchestrator, AutonomousCodingOrchestrator>();

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
    var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Dedicated migration runner started. Applying pending database migrations...");
    await db.Database.MigrateAsync();
    logger.LogInformation("Database migrations successfully applied. Exiting with code 0.");
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
        new
        {
            project.Id,
            project.Name,
            project.Slug,
            project.Status
        });
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
        : Results.Ok(project);
}).RequireRateLimiting("GeneralApi");

// ------------------------------------------------------------
// Human Approval
// ------------------------------------------------------------

app.MapGet("/api/approvals/pending", async (
    ApprovalService service,
    CancellationToken cancellationToken) =>
{
    var pending = await service.GetPendingAsync(cancellationToken);
    return Results.Ok(pending);
}).RequireRateLimiting("GeneralApi");

app.MapPost("/api/approvals/{id}/approve", async (
    string id,
    ApprovalDecisionRequest? request,
    ApprovalService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid approval identifier.");
    }

    if (request?.Comment?.Length > 2000)
    {
        throw new ValidationException("Comment cannot exceed 2000 characters.");
    }

    var approval = await service.ApproveAsync(parsedId, request?.Comment, cancellationToken);
    return Results.Ok(approval);
}).RequireRateLimiting("GeneralApi");

app.MapPost("/api/approvals/{id}/reject", async (
    string id,
    ApprovalDecisionRequest? request,
    ApprovalService service,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out var parsedId) || parsedId == Guid.Empty)
    {
        throw new ValidationException("Invalid approval identifier.");
    }

    if (request?.Comment?.Length > 2000)
    {
        throw new ValidationException("Comment cannot exceed 2000 characters.");
    }

    var approval = await service.RejectAsync(parsedId, request?.Comment, cancellationToken);
    return Results.Ok(approval);
}).RequireRateLimiting("GeneralApi");

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

app.Run();

public sealed record CreateProjectRequest(
    string Name,
    string? Description);

public sealed record ApprovalDecisionRequest(
    string? Comment);

public sealed record StartOrchestrationRunRequest(
    Guid ProjectId,
    Guid TaskId,
    int? MaxAttempts);

// Exposes the top-level-statement entry point to WebApplicationFactory<Program> for integration tests.
public partial class Program
{
}
