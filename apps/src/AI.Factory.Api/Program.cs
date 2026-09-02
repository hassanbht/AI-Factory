using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Approvals;
using AI.Factory.Application.Learning;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Resilience;
using AI.Factory.Api.HealthChecks;
using AI.Factory.Api.Middleware;
using AI.Factory.Application.Resilience;
using AI.Factory.Infrastructure.Validation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------
// Services
// ------------------------------------------------------------

builder.Services.AddOpenApi();

var executionLimits = new ExecutionLimitsOptions();
builder.Configuration.GetSection("ExecutionLimits").Bind(executionLimits);
executionLimits.Validate();
builder.Services.AddSingleton(executionLimits);

builder.Services.AddDbContext<FactoryDbContext>(options =>
{
    var connectionString =
        builder.Configuration.GetConnectionString("FactoryDatabase")
        ?? "Data Source=../../../data/factory.db";

    options.UseSqlite(connectionString);
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

var hermesCircuitBreakerOptions = new CircuitBreakerOptions();
builder.Configuration.GetSection("CircuitBreaker:Hermes").Bind(hermesCircuitBreakerOptions);
builder.Services.AddSingleton(hermesCircuitBreakerOptions);
builder.Services.AddSingleton(sp => new CircuitBreaker(
    hermesCircuitBreakerOptions, sp.GetRequiredService<ILogger<CircuitBreaker>>(), "Hermes"));

var resilienceOptions = new ResilienceOptions();
builder.Configuration.GetSection(ResilienceOptions.SectionName).Bind(resilienceOptions);
resilienceOptions.Validate();
builder.Services.AddSingleton(resilienceOptions);
builder.Services.AddSingleton<IBackoffCalculator, ExponentialBackoffCalculator>();
builder.Services.AddSingleton<IDelayProvider, DefaultDelayProvider>();
builder.Services.AddScoped<IResilienceExecutor, ResilienceExecutor>();

// Decorates HermesAgentExecutor with circuit breaker and centralized resilience
builder.Services.AddScoped<IAgentExecutor>(sp =>
{
    var hermes = sp.GetRequiredService<HermesAgentExecutor>();
    var breaker = sp.GetRequiredService<CircuitBreaker>();
    var breakerDecorator = new CircuitBreakerAgentExecutorDecorator(
        hermes,
        breaker,
        sp.GetRequiredService<ILogger<CircuitBreakerAgentExecutorDecorator>>());

    return new ResilienceAgentExecutorDecorator(
        breakerDecorator,
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
builder.Services.AddSingleton<IOperationScopeFactory, OperationScopeFactory>();
builder.Services.AddSingleton<IFailureClassifier, FailureClassifier>();
builder.Services.AddSingleton<ApiExceptionMapper>();
builder.Services.AddScoped<IOperationalDiagnosticsService, OperationalDiagnosticsService>();

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
// Health checks — kept isolated from business logic (Api/HealthChecks/
// only, no calls into orchestration services). "ready" tag = readiness;
// no tag at all = liveness runs zero checks (process responding is enough).
// ------------------------------------------------------------

var healthChecksBuilder = builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" })
    .AddCheck<ConfigurationHealthCheck>("configuration", tags: new[] { "ready" })
    .AddCheck<CriticalServicesHealthCheck>("critical-services", tags: new[] { "ready" });

if (builder.Configuration.GetValue<bool>("Health:CheckOpenViking"))
{
    healthChecksBuilder.AddCheck<OpenVikingHealthCheck>("openviking", tags: new[] { "ready" });
}

var app = builder.Build();

// ------------------------------------------------------------
// Global exception handling — first in the pipeline so it wraps
// every other middleware/endpoint
// ------------------------------------------------------------

app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();

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

if (app.Environment.IsDevelopment())
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
    var project = await service.CreateAsync(
        request.Name,
        request.Description,
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
});

app.MapGet("/api/projects/{id:guid}", async (
    Guid id,
    IProjectRepository repository,
    CancellationToken cancellationToken) =>
{
    var project = await repository.GetByIdAsync(
        id,
        cancellationToken);

    return project is null
        ? Results.NotFound()
        : Results.Ok(project);
});

// ------------------------------------------------------------
// Human Approval
// ------------------------------------------------------------

app.MapGet("/api/approvals/pending", async (
    ApprovalService service,
    CancellationToken cancellationToken) =>
{
    var pending = await service.GetPendingAsync(cancellationToken);
    return Results.Ok(pending);
});

app.MapPost("/api/approvals/{id:guid}/approve", async (
    Guid id,
    ApprovalDecisionRequest? request,
    ApprovalService service,
    CancellationToken cancellationToken) =>
{
    var approval = await service.ApproveAsync(id, request?.Comment, cancellationToken);
    return Results.Ok(approval);
});

app.MapPost("/api/approvals/{id:guid}/reject", async (
    Guid id,
    ApprovalDecisionRequest? request,
    ApprovalService service,
    CancellationToken cancellationToken) =>
{
    var approval = await service.RejectAsync(id, request?.Comment, cancellationToken);
    return Results.Ok(approval);
});

// ------------------------------------------------------------
// Execution Audit Trail
// ------------------------------------------------------------

app.MapGet("/api/audit-log", async (
    Guid? projectId,
    Guid? executionId,
    int page,
    int pageSize,
    IAuditLogRepository repository,
    CancellationToken cancellationToken) =>
{
    var query = new AuditLogQuery(
        projectId,
        executionId,
        page <= 0 ? 1 : page,
        pageSize <= 0 ? 50 : pageSize);

    var result = await repository.GetPagedAsync(query, cancellationToken);
    return Results.Ok(result);
});

// ------------------------------------------------------------
// Orchestration Runs — real end-to-end pipeline execution
// ------------------------------------------------------------

// ------------------------------------------------------------
// Operational Diagnostics (Phase 17.3)
// ------------------------------------------------------------

app.MapGet("/api/operations/metrics", (IOperationalMetrics metrics) => Results.Ok(metrics.GetSnapshot()));

app.MapGet("/api/operations/recent", async (
    int? count,
    IOperationalDiagnosticsService diagnostics,
    CancellationToken cancellationToken) =>
{
    var entries = await diagnostics.GetRecentOperationsAsync(count ?? 50, cancellationToken);
    return Results.Ok(entries);
});

app.MapGet("/api/operations/{correlationId:guid}", async (
    Guid correlationId,
    IOperationalDiagnosticsService diagnostics,
    CancellationToken cancellationToken) =>
{
    var summary = await diagnostics.GetCorrelationSummaryAsync(correlationId, cancellationToken);
    return summary is null ? Results.NotFound() : Results.Ok(summary);
});

app.MapGet("/api/projects/{projectId:guid}/diagnostics", async (
    Guid projectId,
    IOperationalDiagnosticsService diagnostics,
    CancellationToken cancellationToken) =>
{
    var summary = await diagnostics.GetProjectSummaryAsync(projectId, cancellationToken);
    return summary is null ? Results.NotFound() : Results.Ok(summary);
});

app.MapPost("/api/orchestration-runs", async (
    StartOrchestrationRunRequest request,
    IOrchestrationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.StartAsync(
        new StartOrchestrationCommand(request.ProjectId, request.TaskId, request.MaxAttempts),
        cancellationToken);

    return Results.Accepted(result.StatusUrl, new
    {
        runId = result.RunId,
        status = result.Status,
        statusUrl = result.StatusUrl
    });
});

app.MapGet("/api/orchestration-runs/{id:guid}", async (
    Guid id,
    IOrchestrationService service,
    CancellationToken cancellationToken) =>
{
    var run = await service.GetAsync(id, cancellationToken);

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
});

app.MapGet("/api/orchestration-runs/{id:guid}/attempts", async (
    Guid id,
    IOrchestrationService service,
    CancellationToken cancellationToken) =>
{
    var run = await service.GetAsync(id, cancellationToken);

    return run is null
        ? Results.NotFound()
        : Results.Ok(run.Attempts.OrderBy(a => a.AttemptNumber));
});

app.MapGet("/api/orchestration-runs/{id:guid}/logs", async (
    Guid id,
    IOrchestrationService service,
    CancellationToken cancellationToken) =>
{
    var run = await service.GetAsync(id, cancellationToken);

    return run is null
        ? Results.NotFound()
        : Results.Ok(run.ProgressEvents.OrderBy(e => e.TimestampUtc));
});

app.MapPost("/api/orchestration-runs/{id:guid}/cancel", async (
    Guid id,
    IOrchestrationService service,
    CancellationToken cancellationToken) =>
{
    await service.RequestCancellationAsync(id, cancellationToken);
    return Results.Accepted();
});

app.Run();

public sealed record CreateProjectRequest(
    string Name,
    string Description);

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
