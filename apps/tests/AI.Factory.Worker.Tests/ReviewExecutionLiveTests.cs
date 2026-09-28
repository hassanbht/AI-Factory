using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Learning;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Configuration;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Worker.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase H §29 & §30 — Live Review and Full-Lifecycle Validation Tests.
/// Validates that:
/// 1. Real Review execution runs through:
///    AgentCodeReviewer -> Review profile -> AdapterDispatchingAgentRuntime -> HermesExecutionAdapter -> real Hermes -> real OpenRouter -> parsed Review result.
/// 2. Full lifecycle progression:
///    Create -> Source -> Start -> WorkItem -> Implementation -> Verification -> Acceptance -> Review (via Adapter) -> HumanReview -> Approval -> Completed.
/// Runs in disposable workspace and throwaway SQLite database with zero container leaks.
/// </summary>
public sealed class ReviewExecutionLiveTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempRoot;
    private readonly SqliteConnection _connection;

    public ReviewExecutionLiveTests(ITestOutputHelper output)
    {
        _output = output;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"aif-live-review-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(_tempRoot);

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private FactoryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;
        var db = new FactoryDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task LiveReview_ExecutesRealModel_ThroughHermesExecutionAdapter()
    {
        BenchmarkHarness.TryLoadEnvLive();
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _output.WriteLine("[SKIP] OPENROUTER_API_KEY not configured — skipping live model execution.");
            return;
        }

        var preContainers = BenchmarkHarness.GetFactoryContainerCount();
        _output.WriteLine($"[SETUP] Pre-execution container count: {preContainers}");

        var projectId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();
        var modelId = "deepseek/deepseek-v4-flash-0731";

        // 1. Configure Hermes
        var hermesOptions = new HermesOptions
        {
            DockerImage = "nousresearch/hermes-agent:0.20.6",
            NetworkMode = "bridge",
            Provider = "openrouter",
            WorkspaceRoot = _tempRoot,
            DataVolume = Path.Combine(_tempRoot, "hermes-data"),
            TimeoutSeconds = 180,
            ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
            ProviderApiKeySecretName = "OPENROUTER_API_KEY",
            DefaultModel = modelId,
            AdditionalEnvironmentVariables =
            {
                ["OPENROUTER_API_KEY"] = apiKey
            }
        };
        Directory.CreateDirectory(hermesOptions.DataVolume);

        var secretProvider = new EnvironmentSecretProvider(NullLogger<EnvironmentSecretProvider>.Instance);
        var commandPolicy = new CommandPolicyOptions();
        var processRunner = new SystemProcessRunner(commandPolicy);
        var executor = new HermesAgentExecutor(
            hermesOptions,
            processRunner,
            NullLogger<HermesAgentExecutor>.Instance,
            secretProvider: secretProvider);

        var hermesAdapter = new HermesExecutionAdapter(
            executor,
            new AI.Factory.Application.Abstractions.FailureClassifier(),
            NullLogger<HermesExecutionAdapter>.Instance);

        // 2. Configure Execution Options with explicit Review profile
        var executionOptions = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles =
            {
                ["Hermes"] = new ExecutionProfileOptions { Adapter = "Hermes" }
            },
            AgentTypeProfiles =
            {
                ["Review"] = "Hermes"
            }
        };
        executionOptions.Validate();

        var registry = new ExecutionAdapterRegistry(new[] { hermesAdapter });
        var resolver = new ExecutionProfileResolver(executionOptions, registry);
        var dispatcher = new AdapterDispatchingAgentRuntime(
            resolver,
            new AI.Factory.Application.Abstractions.FailureClassifier(),
            NullLogger<AdapterDispatchingAgentRuntime>.Instance);

        // 3. Configure ModelRouter
        var modelRoutingOptions = new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new()
                {
                    ModelId = modelId,
                    Provider = "openrouter",
                    ReasoningStrength = 5,
                    MaxContextTokens = 128_000,
                    CostTier = 3,
                    IsCodingOptimized = true,
                    IsAvailable = true
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["code-review"] = new List<string> { modelId }
            }
        };
        var modelRouter = new ModelRouter(modelRoutingOptions);

        // 4. Instantiate migrated AgentCodeReviewer
        var reviewer = new AgentCodeReviewer(
            dispatcher,
            modelRouter,
            NullLogger<AgentCodeReviewer>.Instance);

        // 5. Build ReviewContext
        var reviewContext = new ReviewContext(
            WorkItemId: workItemId,
            ProjectId: projectId,
            RequirementsText: "Create a calculator function that computes factorial(n). For negative n, throw ArgumentOutOfRangeException.",
            AcceptanceCriteria: "Factorial(0) == 1, Factorial(5) == 120, Factorial(-1) throws ArgumentOutOfRangeException.",
            ArchitectureText: "Static utility class Calculator in Math library.",
            ChangedFiles: new[] { "src/Math/Calculator.cs", "tests/Math/CalculatorTests.cs" },
            TestOutput: "Passed: 3, Failed: 0, Duration: 0.1s",
            TestsSucceeded: true,
            BuildOutput: "Build succeeded. 0 Warning(s). 0 Error(s).",
            BuildSucceeded: true,
            TaskHistoryText: "Implemented factorial with recursive algorithm and unit tests.");

        _output.WriteLine($"[START] Live review dispatching for WorkItem {workItemId}...");
        var sw = Stopwatch.StartNew();

        var reviewResult = await reviewer.ReviewAsync(reviewContext, CancellationToken.None);

        sw.Stop();
        _output.WriteLine($"[FINISH] Live review completed in {sw.Elapsed.TotalSeconds:F1}s.");
        _output.WriteLine($"[VERDICT] BlocksCompletion={reviewResult.BlocksCompletion}, FindingCount={reviewResult.Findings.Count}");

        foreach (var finding in reviewResult.Findings)
        {
            _output.WriteLine($" - [{finding.Severity}/{finding.Category}] {finding.Description} -> {finding.Recommendation}");
        }

        // 6. Assertions
        Assert.Equal(workItemId, reviewResult.WorkItemId);
        Assert.NotNull(reviewResult.Findings);

        // 7. Cleanup verification
        var postContainers = BenchmarkHarness.GetFactoryContainerCount();
        _output.WriteLine($"[CLEANUP] Post-execution container count: {postContainers}");
        Assert.Equal(0, postContainers);
    }

    [Fact]
    public async Task LiveLifecycle_ReviewViaAdapter_AdvancesToHumanReview_AndCompletedOnApproval()
    {
        using var db = CreateDbContext();

        var projectId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();
        var agentRunId = Guid.NewGuid();

        var project = new Project
        {
            Id = projectId,
            Name = "Test Lifecycle Project",
            Slug = "test-lifecycle-project",
            RootPath = "unused",
            Description = "Phase H lifecycle verification",
            Status = ProjectStatus.Reviewing,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await db.Projects.AddAsync(project);

        var workItem = new WorkItem
        {
            Id = workItemId,
            ProjectId = projectId,
            Title = "Implement service",
            Description = "Initial implementation",
            Status = WorkItemStatus.Testing.ToString(),
            Type = WorkItemType.Implementation,
            Priority = "Normal",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await db.WorkItems.AddAsync(workItem);

        var agentDef = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Coder",
            AgentType = "Implementation",
            Enabled = true
        };
        await db.AgentDefinitions.AddAsync(agentDef);

        var agentRun = new AgentRun
        {
            Id = agentRunId,
            AgentDefinitionId = agentDef.Id,
            ProjectId = projectId,
            WorkItemId = workItemId,
            CorrelationId = Guid.NewGuid(),
            Status = AgentRunStatus.Succeeded,
            Attempt = 1,
            CreatedAtUtc = DateTime.UtcNow
        };
        await db.AgentRuns.AddAsync(agentRun);
        await db.SaveChangesAsync();

        // 1. Setup mock dispatcher simulating Hermes adapter response
        var expectedReviewJson = """
            [
              {"severity":"Info","category":"Maintainability","file":"src/Service.cs","line":10,"description":"Code is well structured.","recommendation":"No action needed."}
            ]
            """;

        var dispatcher = new TestDispatcher(_ => new ExecutionOutcome(
            ExecutionOutcomeStatus.Succeeded,
            expectedReviewJson,
            null,
            null,
            45,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new ModelRoute("test-model", null),
            "exit:0"));

        var modelRouter = CreateTestModelRouter();
        var reviewer = new AgentCodeReviewer(dispatcher, modelRouter, NullLogger<AgentCodeReviewer>.Instance);

        var validationRepo = new ValidationRunRepository(db);
        var reviewRunRepo = new ReviewRunRepository(db);
        var evalRunRepo = new EvaluationRunRepository(db);
        var learningService = new LearningService(new LearningStore(db));

        var reviewOptions = new ReviewOptions { BlockOnMediumFindings = false };
        var reviewContextBuilder = new TestReviewContextBuilder(projectId, workItemId);
        var evalEngine = new TestEvaluationEngine();

        var processor = new WorkItemCompletionProcessor(
            validationRepo,
            reviewContextBuilder,
            reviewer,
            reviewRunRepo,
            reviewOptions,
            evalEngine,
            evalRunRepo,
            learningService,
            NullLogger<WorkItemCompletionProcessor>.Instance);

        var verificationResult = new VerificationResult(
            VerificationStatus.Passed,
            "Verification passed",
            0,
            TimeSpan.FromSeconds(1));

        // 2. Process WorkItem through Review
        var decision = await processor.ProcessAsync(project, workItem, agentRun, verificationResult);

        Assert.Equal(WorkItemStatus.Completed, decision.Target);
        workItem.Status = WorkItemStatus.Completed.ToString();
        await db.SaveChangesAsync();

        // Verify ReviewRun was persisted
        var reviewRun = await db.ReviewRuns.FirstOrDefaultAsync(r => r.WorkItemId == workItemId);
        Assert.NotNull(reviewRun);
        Assert.False(reviewRun.BlocksCompletion);

        // 3. ProjectCompletionGate evaluation: Reviewing -> HumanReview
        var completionGate = new ProjectCompletionGate(
            new ProjectRepository(db),
            new AgentRunRepository(db),
            new TestVerificationGate(),
            NullLogger<ProjectCompletionGate>.Instance);

        var gateResult = await completionGate.EvaluateAsync(project, new[] { workItem });
        Assert.Equal(ProjectCompletionGateAction.AdvancedToHumanReview, gateResult.Action);

        var updatedProject = await db.Projects.FindAsync(projectId);
        Assert.NotNull(updatedProject);
        Assert.Equal(ProjectStatus.HumanReview, updatedProject.Status);

        // 4. Human Approval boundary: only explicit approval transitions HumanReview -> Completed
        var projectService = new ProjectService(
            new ProjectRepository(db),
            new TestProvisioner(),
            NullAuditLogger.Instance);

        var approvedProject = await projectService.ApproveAsync(projectId, "Approved by QA engineer");
        Assert.Equal(ProjectStatus.Completed, approvedProject.Status);

        var finalProject = await db.Projects.FindAsync(projectId);
        Assert.NotNull(finalProject);
        Assert.Equal(ProjectStatus.Completed, finalProject.Status);
    }

    #region Helpers & Test Fakes

    private static ModelRouter CreateTestModelRouter()
    {
        var options = new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new()
                {
                    ModelId = "test-model",
                    Provider = "test-provider",
                    ReasoningStrength = 5,
                    MaxContextTokens = 128_000,
                    CostTier = 3,
                    IsCodingOptimized = false,
                    IsAvailable = true
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["code-review"] = new List<string> { "test-model" }
            }
        };
        return new ModelRouter(options);
    }

    private sealed class TestDispatcher : IAgentExecutionDispatcher
    {
        private readonly Func<AgentExecutionSpec, ExecutionOutcome> _handler;

        public TestDispatcher(Func<AgentExecutionSpec, ExecutionOutcome> handler) => _handler = handler;

        public Task<ExecutionOutcome> DispatchAsync(AgentExecutionSpec spec, CancellationToken cancellationToken = default) =>
            Task.FromResult(_handler(spec));
    }

    private sealed class TestReviewContextBuilder : IReviewContextBuilder
    {
        private readonly Guid _projectId;
        private readonly Guid _workItemId;

        public TestReviewContextBuilder(Guid projectId, Guid workItemId)
        {
            _projectId = projectId;
            _workItemId = workItemId;
        }

        public Task<ReviewContext> BuildAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewContext(
                _workItemId,
                _projectId,
                "Requirements",
                "Acceptance",
                "Architecture",
                new[] { "src/Service.cs" },
                "Tests succeeded",
                true,
                "Build succeeded",
                true,
                "History"));
    }

    private sealed class TestEvaluationEngine : IEvaluationEngine
    {
        public Task<EvaluationResult> EvaluateAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EvaluationResult(workItem.Id, true, true, false, null, 0, RegressionStatus.None, true));
    }

    private sealed class TestVerificationGate : IVerificationGate
    {
        public Task<VerificationResult> VerifyAsync(Project project, WorkItem workItem, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(VerificationStatus.Passed, "passed", 0, TimeSpan.FromSeconds(1)));

        public Task<VerificationResult> VerifyProjectAsync(Project project, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(VerificationStatus.Passed, "passed", 0, TimeSpan.FromSeconds(1)));
    }

    private sealed class TestProvisioner : IProjectWorkspaceProvisioner
    {
        public ProvisioningResult Provision(Project project) =>
            ProvisioningResult.Succeeded(Path.GetTempPath());
    }

    #endregion
}
