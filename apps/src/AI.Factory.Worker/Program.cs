using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Approvals;
using AI.Factory.Application.Learning;
using AI.Factory.Application.Orchestration;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Resilience;
using AI.Factory.Application.Resilience;
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
        builder.Services.AddDbContext<FactoryDbContext>(options =>
{
    var connectionString =
        builder.Configuration.GetConnectionString("FactoryDatabase")
        ?? "Data Source=../../../data/factory.db";

    options.UseSqlite(connectionString);
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

builder.Services.AddSingleton<OperationContextAccessor>();
builder.Services.AddSingleton<IOperationContextAccessor>(sp => sp.GetRequiredService<OperationContextAccessor>());
builder.Services.AddSingleton<IOperationalMetrics, OperationalMetrics>();
builder.Services.AddSingleton<IOperationScopeFactory, OperationScopeFactory>();
builder.Services.AddSingleton<IFailureClassifier, FailureClassifier>();
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
builder.Services.AddScoped<IAutonomousCodingOrchestrator, AutonomousCodingOrchestrator>();
builder.Services.AddScoped<OrchestrationRunExecutionEngine>();

        builder.Services.AddHostedService<WorkflowRecoveryHostedService>();
        builder.Services.AddHostedService<OrchestrationRunWorker>();
    }
}
