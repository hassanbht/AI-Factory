using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Execution-adapter Phase B golden equivalence. The same scripted underlying
/// Hermes execution (a fake <see cref="IAgentExecutor"/> — the boundary below
/// which HermesAgentExecutor/docker would run) is driven through two complete,
/// real AgentOrchestrator pipelines, each on its own isolated database:
///
///   OLD: AgentOrchestrator -> HermesAgentRuntime
///   NEW: AgentOrchestrator -> AdapterDispatchingAgentRuntime -> LegacyHermesRuntimeAdapter -> HermesAgentRuntime
///
/// and everything observable is compared: what reached the executor (agent,
/// model, prompt, working directory — i.e. every input the Hermes CLI/docker
/// arguments are built from), the orchestration result, the persisted AgentRun
/// (status, failure category, error, output, provider, model, tokens, attempt,
/// full structured result incl. metadata), recorded artifacts, and exception /
/// cancellation behavior. Artifact ids are the only normalized values (fresh
/// Guids per database); durations are compared except where HermesAgentRuntime
/// itself measures wall-clock time (the cancellation path).
/// </summary>
public sealed class ExecutionAdapterGoldenEquivalenceTests : IDisposable
{
    private static readonly Guid DefinitionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid WorkItemId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RunId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid CorrelationId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private const string ValidStructuredOutput =
        """{"version":1,"status":"Succeeded","summary":"did it","output":"the answer","artifacts":["agent-said.txt"]}""";

    private readonly string _workspace;

    public ExecutionAdapterGoldenEquivalenceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "golden-equivalence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        File.WriteAllText(Path.Combine(_workspace, "src", "Marker.cs"), "// MARKER");
        File.WriteAllText(Path.Combine(_workspace, "README.md"), "readme");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public enum Scenario
    {
        SuccessWithValidStructuredResult,
        SuccessWithMalformedOutput,
        ProcessFailure,
        ProcessTimeout,
        LaunchFailureMessageOnly,
        CancelledMidFlight,
        ExecutorThrows,
        DisabledDefinition,
        WorkspaceBackedSuccessRecordsArtifacts,
        WorkspaceBackedFailure,
        RoutedModelAndProvider,
        ExplicitModelNoRouting
    }

    [Theory]
    [InlineData(Scenario.SuccessWithValidStructuredResult)]
    [InlineData(Scenario.SuccessWithMalformedOutput)]
    [InlineData(Scenario.ProcessFailure)]
    [InlineData(Scenario.ProcessTimeout)]
    [InlineData(Scenario.LaunchFailureMessageOnly)]
    [InlineData(Scenario.CancelledMidFlight)]
    [InlineData(Scenario.ExecutorThrows)]
    [InlineData(Scenario.DisabledDefinition)]
    [InlineData(Scenario.WorkspaceBackedSuccessRecordsArtifacts)]
    [InlineData(Scenario.WorkspaceBackedFailure)]
    [InlineData(Scenario.RoutedModelAndProvider)]
    [InlineData(Scenario.ExplicitModelNoRouting)]
    public async Task OldAndNewPaths_AreObservablyEquivalent(Scenario scenario)
    {
        var oldPath = await RunAsync(scenario, useDispatcher: false);
        var newPath = await RunAsync(scenario, useDispatcher: true);

        // Sanity: each scenario actually exercised the path it claims to.
        Assert.Equal(ExpectedStatus(scenario), oldPath.PersistedStatus);

        Assert.Equal(oldPath.ExecutorRequests, newPath.ExecutorRequests);
        Assert.Equal(oldPath.ExceptionType, newPath.ExceptionType);
        Assert.Equal(oldPath.ExceptionMessage, newPath.ExceptionMessage);
        Assert.Equal(oldPath.Result, newPath.Result);
        Assert.Equal(oldPath.PersistedStatus, newPath.PersistedStatus);
        Assert.Equal(oldPath.PersistedRun, newPath.PersistedRun);
        Assert.Equal(oldPath.StructuredResultJson, newPath.StructuredResultJson);
        Assert.Equal(oldPath.ArtifactPaths, newPath.ArtifactPaths);

        if (scenario != Scenario.CancelledMidFlight)
        {
            Assert.Equal(oldPath.DurationMs, newPath.DurationMs);
        }
    }

    private static AgentRunStatus ExpectedStatus(Scenario scenario) => scenario switch
    {
        Scenario.SuccessWithValidStructuredResult or Scenario.SuccessWithMalformedOutput
            or Scenario.WorkspaceBackedSuccessRecordsArtifacts or Scenario.RoutedModelAndProvider
            or Scenario.ExplicitModelNoRouting => AgentRunStatus.Succeeded,
        Scenario.ProcessTimeout => AgentRunStatus.TimedOut,
        Scenario.CancelledMidFlight => AgentRunStatus.Cancelled,
        Scenario.ExecutorThrows => AgentRunStatus.Running,
        _ => AgentRunStatus.Failed
    };

    private sealed record ExecutorRequest(string Agent, string? Model, string Prompt, string WorkingDirectory);

    private sealed record ResultSnapshot(
        bool Executed, bool Success, AgentRunStatus FinalStatus, string? Output, string? ErrorMessage,
        FailureCategory? FailureCategory, string ToolCalls, string AvailableTools, Guid CorrelationId);

    private sealed record PersistedRunSnapshot(
        string? Output, string? ErrorMessage, FailureCategory? FailureCategory, string? Provider, string? Model,
        int? PromptTokens, int? CompletionTokens, int? TotalTokens, int Attempt, int? StructuredResultVersion,
        bool HasStartedAt, bool HasCompletedAt);

    private sealed record PathObservation(
        string ExecutorRequests,
        string? ExceptionType,
        string? ExceptionMessage,
        ResultSnapshot? Result,
        long? DurationMs,
        AgentRunStatus PersistedStatus,
        PersistedRunSnapshot PersistedRun,
        string? StructuredResultJson,
        string ArtifactPaths);

    /// <summary>Scripted Hermes process outcome, identical for both paths.</summary>
    private sealed class ScriptedExecutor : IAgentExecutor
    {
        private readonly Scenario _scenario;
        private readonly CancellationTokenSource _cts;

        public ScriptedExecutor(Scenario scenario, CancellationTokenSource cts)
        {
            _scenario = scenario;
            _cts = cts;
        }

        public List<ExecutorRequest> Requests { get; } = new();

        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(new ExecutorRequest(request.Agent, request.Model, request.Prompt, request.WorkingDirectory));
            var workspace = string.IsNullOrEmpty(request.WorkingDirectory) ? "/scratch" : request.WorkingDirectory;

            switch (_scenario)
            {
                case Scenario.SuccessWithMalformedOutput:
                    return Task.FromResult(new AgentExecutionResult(true, "not json at all", null, 120, workspace));
                case Scenario.ProcessFailure:
                case Scenario.WorkspaceBackedFailure:
                    return Task.FromResult(new AgentExecutionResult(false, "partial stdout", "fatal: stderr text", 90, workspace));
                case Scenario.ProcessTimeout:
                    return Task.FromResult(new AgentExecutionResult(
                        false, "stdout before timeout", "Execution timed out after 300 seconds.", 300_000, workspace));
                case Scenario.LaunchFailureMessageOnly:
                    return Task.FromResult(new AgentExecutionResult(false, null, null, 3, workspace));
                case Scenario.CancelledMidFlight:
                    _cts.Cancel();
                    throw new OperationCanceledException(cancellationToken);
                case Scenario.ExecutorThrows:
                    throw new InvalidOperationException("executor exploded");
                default:
                    return Task.FromResult(new AgentExecutionResult(true, ValidStructuredOutput, null, 250, workspace));
            }
        }
    }

    private async Task<PathObservation> RunAsync(Scenario scenario, bool useDispatcher)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(connection).Options;

        await using (var setupDb = new FactoryDbContext(options))
        {
            setupDb.Database.EnsureCreated();
        }

        var workItemBacked = scenario is Scenario.WorkspaceBackedSuccessRecordsArtifacts or Scenario.WorkspaceBackedFailure;
        await SeedAsync(options, scenario, workItemBacked);

        using var cts = new CancellationTokenSource();
        var executor = new ScriptedExecutor(scenario, cts);
        var routing = scenario == Scenario.RoutedModelAndProvider ? RoutingOptions() : null;

        string? exceptionType = null;
        string? exceptionMessage = null;
        AgentOrchestrationResult? result = null;

        await using (var db = new FactoryDbContext(options))
        {
            var hermesRuntime = new HermesAgentRuntime(
                executor,
                new FailureClassifier(),
                new FileSystemArtifactRecorder(db),
                new ArtifactRepository(db),
                NullLogger<HermesAgentRuntime>.Instance);

            IAgentRuntime runtime = hermesRuntime;

            if (useDispatcher)
            {
                var executionOptions = new ExecutionOptions();
                executionOptions.ApplyBuiltInDefault(LegacyHermesRuntimeAdapter.AdapterKey);
                executionOptions.Validate();

                runtime = new AdapterDispatchingAgentRuntime(
                    new ExecutionProfileResolver(
                        executionOptions,
                        new ExecutionAdapterRegistry(new[] { new LegacyHermesRuntimeAdapter(hermesRuntime) })),
                    NullLogger<AdapterDispatchingAgentRuntime>.Instance);
            }

            var orchestrator = new AgentOrchestrator(
                new AgentRunRepository(db),
                new ProjectRepository(db),
                new FakeAgentToolRegistry("ReadArtifact"),
                new FakeAgentMemory(),
                runtime,
                new ProjectWorkspaceResolver(new AgentWorkspaceOptions()),
                new AgentInstructionBuilder(),
                new StructuredAgentResultParser(),
                NullLogger<AgentOrchestrator>.Instance,
                routing is null ? null : new ModelRouter(routing),
                routing);

            try
            {
                result = await orchestrator.ExecuteAsync(RunId, cts.Token);
            }
            catch (Exception ex)
            {
                exceptionType = ex.GetType().FullName;
                exceptionMessage = ex.Message;
            }
        }

        await using var verifyDb = new FactoryDbContext(options);
        var persisted = (await new AgentRunRepository(verifyDb).GetByIdAsync(RunId))!;
        var artifactPaths = await verifyDb.Artifacts
            .Select(a => a.Path)
            .ToListAsync();

        return new PathObservation(
            ExecutorRequests: JsonSerializer.Serialize(executor.Requests),
            ExceptionType: exceptionType,
            ExceptionMessage: exceptionMessage,
            Result: result is null
                ? null
                : new ResultSnapshot(
                    result.Executed, result.Success, result.FinalStatus, result.Output, result.ErrorMessage,
                    result.FailureCategory, string.Join(",", result.ToolCallNames),
                    string.Join(",", result.AvailableToolNames), result.CorrelationId),
            DurationMs: result?.DurationMs,
            PersistedStatus: persisted.Status,
            PersistedRun: new PersistedRunSnapshot(
                persisted.Output, persisted.ErrorMessage, persisted.FailureCategory, persisted.Provider, persisted.Model,
                persisted.PromptTokens, persisted.CompletionTokens, persisted.TotalTokens, persisted.Attempt,
                persisted.StructuredResultVersion, persisted.StartedAtUtc is not null, persisted.CompletedAtUtc is not null),
            StructuredResultJson: NormalizeStructuredResult(persisted.StructuredResult),
            ArtifactPaths: string.Join("|", artifactPaths.OrderBy(p => p, StringComparer.Ordinal)));
    }

    /// <summary>Artifact references are fresh per-database Guids — compared by count/position only.</summary>
    private static string? NormalizeStructuredResult(string? json)
    {
        if (json is null)
        {
            return null;
        }

        var parsed = JsonSerializer.Deserialize<StructuredAgentResult>(json, StructuredAgentResult.JsonOptions)!;
        var normalized = parsed with
        {
            Artifacts = parsed.Artifacts.Select((a, i) => Guid.TryParse(a, out _) ? $"artifact-id-{i}" : a).ToArray()
        };

        return JsonSerializer.Serialize(normalized, StructuredAgentResult.JsonOptions);
    }

    private static ModelRoutingOptions RoutingOptions()
    {
        var options = new ModelRoutingOptions
        {
            Models =
            {
                new ModelCapability
                {
                    ModelId = "routed-model", Provider = "routed-gateway", ReasoningStrength = 4,
                    MaxContextTokens = 200_000, CostTier = 1, IsAvailable = true
                }
            }
        };

        options.CategoryProfiles["coding"] = new List<string> { "routed-model" };
        options.AgentTypeCategories["Implementation"] = "coding";
        return options;
    }

    private async Task SeedAsync(DbContextOptions<FactoryDbContext> options, Scenario scenario, bool workItemBacked)
    {
        await using var db = new FactoryDbContext(options);

        await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Id = DefinitionId,
            Name = "Coder",
            Description = "Implements WorkItems",
            AgentType = "Implementation",
            SystemPrompt = "You are a coding agent.",
            Enabled = scenario != Scenario.DisabledDefinition
        });

        var projects = new ProjectRepository(db);
        await projects.AddAsync(new Project
        {
            Id = ProjectId,
            Name = "Golden Equivalence",
            Slug = "golden-equivalence",
            RootPath = _workspace
        });
        await projects.SaveChangesAsync();

        var runs = new AgentRunRepository(db);
        var run = await runs.CreateAsync(new AgentRun
        {
            Id = RunId,
            CorrelationId = CorrelationId,
            AgentDefinitionId = DefinitionId,
            ProjectId = ProjectId,
            WorkItemId = workItemBacked ? WorkItemId : null,
            Input = "Implement the marker change.",
            Model = scenario == Scenario.ExplicitModelNoRouting ? "explicit-model" : null,
            Provider = scenario == Scenario.ExplicitModelNoRouting ? "explicit-gateway" : null
        });

        run.TransitionTo(AgentRunStatus.Queued);
        await runs.UpdateAsync(run);
    }
}
