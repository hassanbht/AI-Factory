using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.26 §20 — deterministic coverage of the three things this phase
/// added: role-based model routing with a strictly-bounded fallback,
/// persisted model/cost telemetry, and AgentRun orphan recovery.
///
/// The router, orchestrator and repository under test are the production
/// ones, against real SQLite. Only the agent runtime is faked — the point of
/// these tests is which model the Factory selects and what it persists, not
/// what a model replies.
/// </summary>
public sealed class ModelRoutingAndRecoveryTests : IDisposable
{
    private const string PrimaryModel = "primary/model-a";
    private const string FallbackModel = "fallback/model-b";
    private const string Category = "coding";
    private const string AgentType = "Implementation";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public ModelRoutingAndRecoveryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---------------- Routing ----------------

    [Fact]
    public void SelectCandidates_ReturnsPrimaryFirstThenFallbacks_InConfiguredOrder()
    {
        var router = new ModelRouter(RoutingOptions());

        var candidates = router.SelectCandidates(
            new ModelRoutingRequest(Category, TaskComplexity.Standard, 16_000, CostPreference.Balanced));

        Assert.Equal(new[] { PrimaryModel, FallbackModel }, candidates.Select(c => c.ModelId));
        Assert.Equal(PrimaryModel, router.SelectModel(
            new ModelRoutingRequest(Category, TaskComplexity.Standard, 16_000, CostPreference.Balanced)).ModelId);
    }

    [Fact]
    public async Task ExecuteAsync_RoleModel_IsSelectedAndPersistedOnTheAgentRun()
    {
        var definition = await SeedDefinitionAsync();
        var run = await SeedQueuedRunAsync(definition.Id);
        var runtime = new RecordingRuntime(RuntimeOutcome.Success);

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, runtime).ExecuteAsync(run.Id);
        }

        Assert.Equal(new[] { PrimaryModel }, runtime.ModelsSeen);

        // §13 — post-run analysis must not depend on transient logs.
        var persisted = await ReloadAsync(run.Id);
        Assert.Equal(PrimaryModel, persisted.Model);
        Assert.Equal("test-provider", persisted.Provider);
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitAgentRunModel_Wins_AndDisablesFallback()
    {
        var definition = await SeedDefinitionAsync();
        var run = await SeedQueuedRunAsync(definition.Id, model: "operator/pinned-model");
        var runtime = new RecordingRuntime(RuntimeOutcome.ProviderFailure);

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, runtime).ExecuteAsync(run.Id);
        }

        // Even though the failure class would otherwise permit a fallback, a
        // deliberately pinned model is never silently replaced.
        Assert.Equal(new[] { "operator/pinned-model" }, runtime.ModelsSeen);
        Assert.Equal("operator/pinned-model", (await ReloadAsync(run.Id)).Model);
    }

    [Theory]
    [InlineData(RuntimeOutcome.ProviderFailure)]
    [InlineData(RuntimeOutcome.ModelFailure)]
    [InlineData(RuntimeOutcome.InfrastructureFailure)]
    public async Task ExecuteAsync_InfrastructureClassFailure_FallsBackOnceToTheNextCandidate(RuntimeOutcome outcome)
    {
        var definition = await SeedDefinitionAsync();
        var run = await SeedQueuedRunAsync(definition.Id);
        var runtime = new RecordingRuntime(outcome);

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, runtime).ExecuteAsync(run.Id);
        }

        Assert.Equal(new[] { PrimaryModel, FallbackModel }, runtime.ModelsSeen);

        var persisted = await ReloadAsync(run.Id);
        Assert.Equal(FallbackModel, persisted.Model);
        Assert.Equal(2, persisted.Attempt);
    }

    /// <summary>
    /// §10 — the rule that matters most: a failed test, a failed acceptance
    /// contract, invalid agent output or a genuine coding failure is a RESULT.
    /// Re-running it on another model would launder Factory validation into a
    /// retry, so fallback must not fire for any of them.
    /// </summary>
    [Theory]
    [InlineData(RuntimeOutcome.TestFailure)]
    [InlineData(RuntimeOutcome.ValidationFailure)]
    [InlineData(RuntimeOutcome.AgentFailure)]
    [InlineData(RuntimeOutcome.Timeout)]
    public async Task ExecuteAsync_NonInfrastructureFailure_NeverFallsBack(RuntimeOutcome outcome)
    {
        var definition = await SeedDefinitionAsync();
        var run = await SeedQueuedRunAsync(definition.Id);
        var runtime = new RecordingRuntime(outcome);

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, runtime).ExecuteAsync(run.Id);
        }

        Assert.Equal(new[] { PrimaryModel }, runtime.ModelsSeen);

        var persisted = await ReloadAsync(run.Id);
        Assert.Equal(PrimaryModel, persisted.Model);
        Assert.Equal(1, persisted.Attempt);
    }

    [Fact]
    public async Task ExecuteAsync_SingleCandidateProfile_HasNothingToFallBackTo()
    {
        var definition = await SeedDefinitionAsync();
        var run = await SeedQueuedRunAsync(definition.Id);
        var runtime = new RecordingRuntime(RuntimeOutcome.ProviderFailure);

        var singleCandidate = RoutingOptions();
        singleCandidate.CategoryProfiles[Category] = new List<string> { PrimaryModel };

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, runtime, singleCandidate).ExecuteAsync(run.Id);
        }

        Assert.Equal(new[] { PrimaryModel }, runtime.ModelsSeen);
    }

    // ---------------- Cost telemetry ----------------

    /// <summary>
    /// §14 — the Hermes path supplies no usage data at all. The correct
    /// behaviour is therefore "usage unavailable": nulls stay null, and
    /// nothing anywhere invents a token count or derives a cost from elapsed
    /// time. This test exists to make any future fabrication fail loudly.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenRuntimeSuppliesNoUsage_TokenCountsStayNull()
    {
        var definition = await SeedDefinitionAsync();
        var run = await SeedQueuedRunAsync(definition.Id);

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, new RecordingRuntime(RuntimeOutcome.Success)).ExecuteAsync(run.Id);
        }

        var persisted = await ReloadAsync(run.Id);
        Assert.Null(persisted.PromptTokens);
        Assert.Null(persisted.CompletionTokens);
        Assert.Null(persisted.TotalTokens);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRuntimeSuppliesUsage_ItIsPersistedVerbatim()
    {
        var definition = await SeedDefinitionAsync();
        var run = await SeedQueuedRunAsync(definition.Id);

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, new RecordingRuntime(RuntimeOutcome.SuccessWithUsage)).ExecuteAsync(run.Id);
        }

        var persisted = await ReloadAsync(run.Id);
        Assert.Equal(11, persisted.PromptTokens);
        Assert.Equal(22, persisted.CompletionTokens);
        Assert.Equal(33, persisted.TotalTokens);
    }

    // ---------------- AgentRun orphan recovery ----------------

    [Fact]
    public async Task RecoverOrphanedRunsAsync_Startup_TerminatesEveryRunningRun_WithACrashMarker()
    {
        var definition = await SeedDefinitionAsync();
        var run = await SeedRunningRunAsync(definition.Id);

        int recovered;
        await using (var db = new FactoryDbContext(_options))
        {
            recovered = await new AgentRunRepository(db).RecoverOrphanedRunsAsync(startedBeforeUtc: null);
        }

        Assert.Equal(1, recovered);

        var persisted = await ReloadAsync(run.Id);
        Assert.Equal(AgentRunStatus.Failed, persisted.Status);
        Assert.Equal(FailureCategory.Infrastructure, persisted.FailureCategory);
        Assert.True(AgentRunRecovery.WasOrphaned(persisted));
        Assert.NotNull(persisted.CompletedAtUtc);
    }

    [Fact]
    public async Task RecoverOrphanedRunsAsync_Periodic_LeavesAFreshlyStartedRunUntouched()
    {
        var definition = await SeedDefinitionAsync();
        var run = await SeedRunningRunAsync(definition.Id);

        int recovered;
        await using (var db = new FactoryDbContext(_options))
        {
            // Cutoff in the past: a run that started just now is not stale.
            recovered = await new AgentRunRepository(db)
                .RecoverOrphanedRunsAsync(DateTime.UtcNow - TimeSpan.FromMinutes(15));
        }

        Assert.Equal(0, recovered);
        Assert.Equal(AgentRunStatus.Running, (await ReloadAsync(run.Id)).Status);
    }

    [Fact]
    public async Task RecoverOrphanedRunsAsync_IsIdempotent_AndNeverTouchesTerminalRuns()
    {
        var definition = await SeedDefinitionAsync();
        var run = await SeedRunningRunAsync(definition.Id);

        await using (var db = new FactoryDbContext(_options))
        {
            Assert.Equal(1, await new AgentRunRepository(db).RecoverOrphanedRunsAsync(null));
        }

        await using (var db = new FactoryDbContext(_options))
        {
            // Second sweep finds nothing: the run is terminal now, so recovery
            // can never resurrect or double-count it.
            Assert.Equal(0, await new AgentRunRepository(db).RecoverOrphanedRunsAsync(null));
        }

        Assert.Equal(AgentRunStatus.Failed, (await ReloadAsync(run.Id)).Status);
    }

    /// <summary>
    /// §7 — the invariant the whole recovery workstream exists for. A Project
    /// whose Worker died mid-run must not stay blocked by
    /// HasActiveRunsForProjectAsync forever.
    /// </summary>
    [Fact]
    public async Task RecoverOrphanedRunsAsync_UnblocksTheProjectCompletionGate()
    {
        var definition = await SeedDefinitionAsync();
        var projectId = await SeedProjectAsync();
        await SeedRunningRunAsync(definition.Id, projectId);

        await using (var db = new FactoryDbContext(_options))
        {
            Assert.True(await new AgentRunRepository(db).HasActiveRunsForProjectAsync(projectId));
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await new AgentRunRepository(db).RecoverOrphanedRunsAsync(null);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            Assert.False(await new AgentRunRepository(db).HasActiveRunsForProjectAsync(projectId));
        }
    }

    // ---------------- Helpers ----------------

    private static ModelRoutingOptions RoutingOptions()
    {
        var options = new ModelRoutingOptions
        {
            Models =
            {
                new ModelCapability
                {
                    ModelId = PrimaryModel, Provider = "test-provider", ReasoningStrength = 4,
                    MaxContextTokens = 200_000, CostTier = 1, IsAvailable = true
                },
                new ModelCapability
                {
                    ModelId = FallbackModel, Provider = "test-provider", ReasoningStrength = 4,
                    MaxContextTokens = 200_000, CostTier = 2, IsAvailable = true
                }
            }
        };

        options.CategoryProfiles[Category] = new List<string> { PrimaryModel, FallbackModel };
        options.AgentTypeCategories[AgentType] = Category;
        return options;
    }

    private static AgentOrchestrator CreateOrchestrator(
        FactoryDbContext db, IAgentRuntime runtime, ModelRoutingOptions? routing = null)
    {
        var options = routing ?? RoutingOptions();

        return new AgentOrchestrator(
            new AgentRunRepository(db),
            new ProjectRepository(db),
            new NoToolsRegistry(),
            new NoMemory(),
            runtime,
            new ProjectWorkspaceResolver(new AgentWorkspaceOptions()),
            new AgentInstructionBuilder(),
            new StructuredAgentResultParser(),
            NullLogger<AgentOrchestrator>.Instance,
            new ModelRouter(options),
            options);
    }

    private async Task<AgentDefinition> SeedDefinitionAsync()
    {
        await using var db = new FactoryDbContext(_options);
        return await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = "Implementer",
            Description = "Implements WorkItems",
            AgentType = AgentType,
            SystemPrompt = "You implement."
        });
    }

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = new FactoryDbContext(_options);

        var project = new Project
        {
            Name = "Routing " + Guid.NewGuid().ToString("N")[..8],
            Slug = "routing-" + Guid.NewGuid().ToString("N")[..8],
            Description = "d",
            Status = ProjectStatus.Implementation,
            RootPath = "projects/routing"
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<AgentRun> SeedQueuedRunAsync(Guid definitionId, string? model = null)
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);

        var run = await repo.CreateAsync(new AgentRun
        {
            AgentDefinitionId = definitionId,
            Input = "do the thing",
            Model = model
        });

        run.TransitionTo(AgentRunStatus.Queued);
        await repo.UpdateAsync(run);
        return run;
    }

    private async Task<AgentRun> SeedRunningRunAsync(Guid definitionId, Guid? projectId = null)
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);

        var run = await repo.CreateAsync(new AgentRun
        {
            AgentDefinitionId = definitionId,
            ProjectId = projectId,
            Input = "do the thing"
        });

        run.TransitionTo(AgentRunStatus.Queued);
        run.TransitionTo(AgentRunStatus.Running);
        await repo.UpdateAsync(run);
        return run;
    }

    private async Task<AgentRun> ReloadAsync(Guid runId)
    {
        await using var db = new FactoryDbContext(_options);
        return (await new AgentRunRepository(db).GetByIdAsync(runId))!;
    }

    public enum RuntimeOutcome
    {
        Success,
        SuccessWithUsage,
        ProviderFailure,
        ModelFailure,
        InfrastructureFailure,
        TestFailure,
        ValidationFailure,
        AgentFailure,
        Timeout
    }

    /// <summary>Records the model it was asked to run with, per attempt.</summary>
    private sealed class RecordingRuntime : IAgentRuntime
    {
        private readonly RuntimeOutcome _outcome;

        public RecordingRuntime(RuntimeOutcome outcome) => _outcome = outcome;

        public List<string?> ModelsSeen { get; } = new();

        public Task<AgentRunExecutionResult> ExecuteAsync(
            AgentRun agentRun, CancellationToken cancellationToken = default)
        {
            ModelsSeen.Add(agentRun.Model);

            var (success, category) = _outcome switch
            {
                RuntimeOutcome.Success or RuntimeOutcome.SuccessWithUsage => (true, (FailureCategory?)null),
                RuntimeOutcome.ProviderFailure => (false, FailureCategory.Provider),
                RuntimeOutcome.ModelFailure => (false, FailureCategory.Model),
                RuntimeOutcome.InfrastructureFailure => (false, FailureCategory.Infrastructure),
                RuntimeOutcome.TestFailure => (false, FailureCategory.Test),
                RuntimeOutcome.ValidationFailure => (false, FailureCategory.Validation),
                RuntimeOutcome.AgentFailure => (false, FailureCategory.Agent),
                _ => (false, FailureCategory.Timeout)
            };

            var withUsage = _outcome == RuntimeOutcome.SuccessWithUsage;

            return Task.FromResult(new AgentRunExecutionResult(
                Success: success,
                Output: success ? "{\"status\":\"success\"}" : null,
                PromptTokens: withUsage ? 11 : null,
                CompletionTokens: withUsage ? 22 : null,
                TotalTokens: withUsage ? 33 : null,
                DurationMs: 5,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                ErrorMessage: success ? null : $"simulated {category} failure",
                FailureCategory: category,
                Provider: agentRun.Provider,
                Model: agentRun.Model));
        }
    }

    private sealed class NoToolsRegistry : IAgentToolRegistry
    {
        public IReadOnlyList<AgentToolDescriptor> GetDescriptors() => Array.Empty<AgentToolDescriptor>();

        public Task<AgentToolResult> ExecuteAsync(
            string toolName, AgentToolContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("These tests never invoke a tool.");
    }

    private sealed class NoMemory : IAgentMemory
    {
        public Task StoreAsync(AgentMemoryEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AgentMemoryEntry>> SearchAsync(
            AgentMemoryQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentMemoryEntry>>(Array.Empty<AgentMemoryEntry>());
    }
}
