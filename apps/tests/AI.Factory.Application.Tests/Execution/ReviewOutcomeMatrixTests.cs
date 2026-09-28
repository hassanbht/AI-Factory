using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase H §13, §14, §15, §16, §17 — Comprehensive Review Outcome Matrix Tests.
/// Proves deterministic handling of every supported review execution outcome:
/// - Clean success (Approved, non-blocking)
/// - Rejection (High/Critical issues block completion)
/// - Non-blocking findings (Low/Info issues allow progression)
/// - Malformed reviewer output (degrades to Info, non-blocking)
/// - Runtime launch/execution failure (produces Critical finding, blocks completion)
/// - Authoritative Timeout (produces Critical finding, blocks completion)
/// - Authoritative Cancellation (produces Critical finding, blocks completion)
/// - Adapter unavailable / disabled (fails closed, blocks completion)
/// - Configuration failure / unknown profile (fails closed, blocks completion)
/// - Routed model capability unsatisfied (fails closed, blocks completion)
/// </summary>
public sealed class ReviewOutcomeMatrixTests
{
    private sealed class MockExecutionAdapter : IExecutionAdapter
    {
        private readonly Func<AgentExecutionSpec, ExecutionOutcome> _execute;

        public MockExecutionAdapter(
            string key,
            Func<AgentExecutionSpec, ExecutionOutcome> execute,
            ExecutionCapabilities capabilities = ExecutionCapabilities.FactoryRoutedModel)
        {
            Key = key;
            _execute = execute;
            Capabilities = capabilities;
        }

        public string Key { get; }
        public ExecutionCapabilities Capabilities { get; }

        public AgentExecutionSpec? LastSpec { get; private set; }

        public Task<ExecutionOutcome> ExecuteAsync(AgentExecutionSpec spec, CancellationToken cancellationToken = default)
        {
            LastSpec = spec;
            return Task.FromResult(_execute(spec));
        }
    }

    private static ReviewContext CreateContext() => new(
        WorkItemId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
        ProjectId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
        RequirementsText: "Build invoice generation service.",
        AcceptanceCriteria: "Generates valid PDF invoices with line items and VAT calculation.",
        ArchitectureText: "Service layer pattern.",
        ChangedFiles: new[] { "src/Billing/InvoiceService.cs" },
        TestOutput: "Tests passed: 5",
        TestsSucceeded: true,
        BuildOutput: "Build succeeded.",
        BuildSucceeded: true,
        TaskHistoryText: "Implemented PDF renderer.");

    private static ModelRouter CreateModelRouter()
    {
        var options = new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new()
                {
                    ModelId = "review-model-v1",
                    Provider = "review-provider",
                    ReasoningStrength = 5,
                    MaxContextTokens = 128_000,
                    CostTier = 3,
                    IsCodingOptimized = false,
                    IsAvailable = true
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["code-review"] = new List<string> { "review-model-v1" }
            }
        };

        return new ModelRouter(options);
    }

    private static (AgentCodeReviewer reviewer, MockExecutionAdapter adapter) CreateReviewer(
        Func<AgentExecutionSpec, ExecutionOutcome> handler,
        ExecutionOptions? customOptions = null,
        ExecutionCapabilities capabilities = ExecutionCapabilities.FactoryRoutedModel)
    {
        var options = customOptions ?? new ExecutionOptions
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
        options.Validate();

        var adapter = new MockExecutionAdapter("Hermes", handler, capabilities);
        var registry = new ExecutionAdapterRegistry(new[] { adapter });
        var resolver = new ExecutionProfileResolver(options, registry);
        var runtime = new AdapterDispatchingAgentRuntime(
            resolver,
            new AI.Factory.Application.Abstractions.FailureClassifier(),
            NullLogger<AdapterDispatchingAgentRuntime>.Instance);

        var reviewer = new AgentCodeReviewer(
            runtime,
            CreateModelRouter(),
            NullLogger<AgentCodeReviewer>.Instance);

        return (reviewer, adapter);
    }

    [Fact]
    public async Task Success_EmptyFindings_DoesNotBlock()
    {
        var (reviewer, adapter) = CreateReviewer(spec => new ExecutionOutcome(
            ExecutionOutcomeStatus.Succeeded,
            "[]",
            null,
            null,
            50,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            spec.Model,
            "exit:0"));

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.False(result.BlocksCompletion);
        Assert.Empty(result.Findings);
        Assert.NotNull(adapter.LastSpec);
        Assert.Equal("Review", adapter.LastSpec!.Agent.AgentType);
        Assert.Equal("reviewer", adapter.LastSpec.Agent.Name);
        Assert.True(string.IsNullOrEmpty(adapter.LastSpec.Workspace.Path));
    }

    [Fact]
    public async Task Rejection_CriticalFinding_BlocksCompletion()
    {
        var json = """
            [
              {"severity":"Critical","category":"Security","file":"src/Billing/InvoiceService.cs","line":88,"description":"Insecure direct object reference exposes other tenants' invoices.","recommendation":"Filter by TenantId in database query."}
            ]
            """;

        var (reviewer, _) = CreateReviewer(spec => new ExecutionOutcome(
            ExecutionOutcomeStatus.Succeeded,
            json,
            null,
            null,
            120,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            spec.Model,
            "exit:0"));

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.True(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
        Assert.Equal(ReviewCategory.Security, result.Findings[0].Category);
    }

    [Fact]
    public async Task Rejection_HighFinding_BlocksCompletion()
    {
        var json = """
            [
              {"severity":"High","category":"RequirementsCompliance","file":"src/Billing/InvoiceService.cs","line":105,"description":"VAT rounding does not comply with tax regulations.","recommendation":"Round half to even on line items."}
            ]
            """;

        var (reviewer, _) = CreateReviewer(spec => new ExecutionOutcome(
            ExecutionOutcomeStatus.Succeeded,
            json,
            null,
            null,
            90,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            spec.Model,
            "exit:0"));

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.True(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.High, result.Findings[0].Severity);
    }

    [Fact]
    public async Task NonBlocking_MediumSeverity_DoesNotBlock()
    {
        var json = """
            [
              {"severity":"Medium","category":"Architecture","file":"src/Billing/InvoiceService.cs","line":150,"description":"Synchronous disk I/O in render loop.","recommendation":"Use async streams."}
            ]
            """;

        var (reviewer, _) = CreateReviewer(spec => new ExecutionOutcome(
            ExecutionOutcomeStatus.Succeeded,
            json,
            null,
            null,
            80,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            spec.Model,
            "exit:0"));

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.False(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Medium, result.Findings[0].Severity);
        Assert.Equal(ReviewCategory.Architecture, result.Findings[0].Category);
    }

    [Fact]
    public async Task NonBlocking_LowAndInfoFindings_DoesNotBlock()
    {
        var json = """
            [
              {"severity":"Low","category":"Maintainability","file":"src/Billing/InvoiceService.cs","line":12,"description":"Method exceeds 50 lines.","recommendation":"Extract calculation helper."},
              {"severity":"Info","category":"TestCoverage","file":null,"line":null,"description":"Coverage is acceptable at 88%.","recommendation":"Consider edge case tests."}
            ]
            """;

        var (reviewer, _) = CreateReviewer(spec => new ExecutionOutcome(
            ExecutionOutcomeStatus.Succeeded,
            json,
            null,
            null,
            75,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            spec.Model,
            "exit:0"));

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.False(result.BlocksCompletion);
        Assert.Equal(2, result.Findings.Count);
    }

    [Fact]
    public async Task MalformedOutput_DegradesToSingleInfoFinding_DoesNotBlock()
    {
        var unparseable = "The code looks solid overall. No major issues observed.";

        var (reviewer, _) = CreateReviewer(spec => new ExecutionOutcome(
            ExecutionOutcomeStatus.Succeeded,
            unparseable,
            null,
            null,
            60,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            spec.Model,
            "exit:0"));

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.False(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Info, result.Findings[0].Severity);
        Assert.Equal(ReviewCategory.Maintainability, result.Findings[0].Category);
        Assert.Contains("could not be parsed as structured findings", result.Findings[0].Description);
        Assert.Contains(unparseable, result.Findings[0].Description);
    }

    [Fact]
    public async Task RuntimeFailure_ProducesCriticalFinding_BlocksCompletion()
    {
        var (reviewer, _) = CreateReviewer(_ => new ExecutionOutcome(
            ExecutionOutcomeStatus.Failed,
            null,
            "Process failed to start: binary not found.",
            FailureCategory.Infrastructure,
            10,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new ModelRoute("review-model-v1", null),
            "exit:127"));

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.True(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
        Assert.Equal(ReviewCategory.Correctness, result.Findings[0].Category);
        Assert.Contains("binary not found", result.Findings[0].Description);
    }

    [Fact]
    public async Task AuthoritativeTimeout_ProducesCriticalFinding_BlocksCompletion()
    {
        var (reviewer, _) = CreateReviewer(_ => new ExecutionOutcome(
            ExecutionOutcomeStatus.TimedOut,
            null,
            "Review execution exceeded deadline.",
            FailureCategory.Timeout,
            300_000,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new ModelRoute("review-model-v1", null),
            "timeout"));

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.True(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
        Assert.Contains("exceeded deadline", result.Findings[0].Description);
    }

    [Fact]
    public async Task Cancellation_ProducesCriticalFinding_BlocksCompletion()
    {
        var (reviewer, _) = CreateReviewer(_ => new ExecutionOutcome(
            ExecutionOutcomeStatus.Cancelled,
            null,
            "Review was cancelled by host.",
            FailureCategory.Cancelled,
            500,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new ModelRoute("review-model-v1", null),
            "cancelled"));

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.True(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
        Assert.Contains("cancelled by host", result.Findings[0].Description);
    }

    [Fact]
    public async Task DisabledAdapter_FailsClosed_ProducesCriticalFinding_BlocksCompletion()
    {
        var options = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles =
            {
                ["Hermes"] = new ExecutionProfileOptions { Adapter = "Hermes" }
            },
            AgentTypeProfiles =
            {
                ["Review"] = "Hermes"
            },
            Adapters =
            {
                ["Hermes"] = new ExecutionAdapterOptions { Enabled = false }
            }
        };
        options.Validate();

        var (reviewer, _) = CreateReviewer(_ => throw new InvalidOperationException("Should never be invoked"), options);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.True(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
        Assert.Contains("disabled", result.Findings[0].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapabilityUnsatisfied_CannotHonourRoutedModel_FailsClosed()
    {
        // Adapter without FactoryRoutedModel capability
        var (reviewer, _) = CreateReviewer(
            _ => throw new InvalidOperationException("Should never be invoked"),
            capabilities: ExecutionCapabilities.None);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.True(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
        Assert.Contains("cannot honour the routed model", result.Findings[0].Description);
    }
}
