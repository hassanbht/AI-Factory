using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Execution-adapter golden equivalence, Phase C.
///
/// Phase B proved, scenario by scenario, that the old path
///   AgentOrchestrator -> HermesAgentRuntime
/// and the dispatcher path
///   AgentOrchestrator -> AdapterDispatchingAgentRuntime -> LegacyHermesRuntimeAdapter -> HermesAgentRuntime
/// were observably identical. Before HermesAgentRuntime was removed, the old path's
/// observations for all 12 scenarios were recorded (on commit 5273f53) into
/// <c>Golden/phase-b-baseline.json</c>. This test drives the same scripted Hermes
/// execution (a fake <see cref="IAgentExecutor"/> — the boundary below which
/// HermesAgentExecutor/docker would run) through the Phase C path
///   AgentOrchestrator -> AdapterDispatchingAgentRuntime -> HermesExecutionAdapter
/// with Factory-side artifact recording wired exactly as the composition roots
/// wire it, and compares against that baseline: executor requests, exceptions,
/// orchestration result, duration, persisted AgentRun, structured result and
/// recorded artifacts.
///
/// The one intended difference: structured-result metadata now also carries
/// <c>ExecutionAdapter</c> and <c>ExitReason</c> for runs an adapter executed. Those
/// two keys are asserted explicitly per scenario and then removed before the
/// baseline comparison; every other byte must match. Phase J likewise adds <c>Gateway</c>
/// (and <c>GatewayFailure</c> on a gateway-reported failure), removed here the same way. Normalized values: artifact
/// ids (fresh Guids per database), the temp workspace path and line endings
/// (the baseline was captured on Windows).
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

    private static readonly JsonSerializerOptions BaselineJson = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

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

    [Fact]
    public void Baseline_CoversEveryScenario_ExactlyOnce()
    {
        Assert.Equal(
            Enum.GetNames<Scenario>().OrderBy(n => n, StringComparer.Ordinal),
            LoadBaseline().Select(b => b.Scenario).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(Scenario.SuccessWithValidStructuredResult, "exit:0")]
    [InlineData(Scenario.SuccessWithMalformedOutput, "exit:0")]
    [InlineData(Scenario.ProcessFailure, "exit:1")]
    [InlineData(Scenario.ProcessTimeout, "timeout")]
    [InlineData(Scenario.LaunchFailureMessageOnly, "error")]
    [InlineData(Scenario.CancelledMidFlight, "cancelled")]
    [InlineData(Scenario.ExecutorThrows, null)]
    [InlineData(Scenario.DisabledDefinition, null)]
    [InlineData(Scenario.WorkspaceBackedSuccessRecordsArtifacts, "exit:0")]
    [InlineData(Scenario.WorkspaceBackedFailure, "exit:1")]
    [InlineData(Scenario.RoutedModelAndProvider, "exit:0")]
    [InlineData(Scenario.ExplicitModelNoRouting, "exit:0")]
    public async Task PhaseCPath_MatchesTheRecordedPhaseBBaseline(Scenario scenario, string? expectedExitReason)
    {
        var baseline = LoadBaseline().Single(b => b.Scenario == scenario.ToString());
        var actual = await RunAsync(scenario);

        // Sanity: each scenario actually exercised the path it claims to.
        Assert.Equal(ExpectedStatus(scenario).ToString(), baseline.PersistedStatus);

        Assert.Equal(Normalize(baseline.ExecutorRequests), actual.ExecutorRequests);
        Assert.Equal(baseline.ExceptionType, actual.ExceptionType);
        Assert.Equal(baseline.ExceptionMessage, actual.ExceptionMessage);
        Assert.Equal(baseline.Result, actual.Result);
        Assert.Equal(baseline.PersistedStatus, actual.PersistedStatus.ToString());
        Assert.Equal(baseline.PersistedRun, actual.PersistedRun);
        Assert.Equal(baseline.ArtifactPaths, actual.ArtifactPaths);

        if (scenario != Scenario.CancelledMidFlight)
        {
            Assert.Equal(baseline.DurationMs, actual.DurationMs);
        }

        // The documented Phase C addition, then byte-for-byte equality without it.
        if (expectedExitReason is null)
        {
            Assert.Null(actual.ExecutionAdapter);
            Assert.Null(actual.ExitReason);
        }
        else
        {
            Assert.Equal("Hermes", actual.ExecutionAdapter);
            Assert.Equal(expectedExitReason, actual.ExitReason);
        }

        Assert.Equal(baseline.StructuredResult, actual.StructuredResultJson);
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

    private sealed record BaselineEntry(
        string Scenario,
        List<ExecutorRequest> ExecutorRequests,
        string? ExceptionType,
        string? ExceptionMessage,
        ResultSnapshot? Result,
        long? DurationMs,
        string PersistedStatus,
        PersistedRunSnapshot PersistedRun,
        string? StructuredResult,
        string[] ArtifactPaths);

    private sealed record PathObservation(
        string ExecutorRequests,
        string? ExceptionType,
        string? ExceptionMessage,
        ResultSnapshot? Result,
        long? DurationMs,
        AgentRunStatus PersistedStatus,
        PersistedRunSnapshot PersistedRun,
        string? StructuredResultJson,
        string? ExecutionAdapter,
        string? ExitReason,
        string[] ArtifactPaths);

    /// <summary>
    /// Scripted Hermes process outcome — the same outputs, errors and durations the
    /// baseline was recorded with. Exit codes are now also reported (the field did
    /// not exist in Phase B and was never observable there), mirroring what
    /// HermesAgentExecutor reports for the equivalent process result.
    /// </summary>
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
                    return Task.FromResult(new AgentExecutionResult(true, "not json at all", null, 120, workspace, ExitCode: 0));
                case Scenario.ProcessFailure:
                case Scenario.WorkspaceBackedFailure:
                    return Task.FromResult(new AgentExecutionResult(
                        false, "partial stdout", "fatal: stderr text", 90, workspace, ExitCode: 1));
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
                    return Task.FromResult(new AgentExecutionResult(true, ValidStructuredOutput, null, 250, workspace, ExitCode: 0));
            }
        }
    }

    private async Task<PathObservation> RunAsync(Scenario scenario)
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
            var executionOptions = new ExecutionOptions();
            executionOptions.ApplyBuiltInDefault(HermesExecutionAdapter.AdapterKey);
            executionOptions.Validate();

            var failureClassifier = new FailureClassifier();
            var runtime = new AdapterDispatchingAgentRuntime(
                new ExecutionProfileResolver(
                    executionOptions,
                    new ExecutionAdapterRegistry(new[]
                    {
                        new HermesExecutionAdapter(executor, failureClassifier, NullLogger<HermesExecutionAdapter>.Instance)
                    })),
                failureClassifier,
                NullLogger<AdapterDispatchingAgentRuntime>.Instance);

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
                routing,
                new FileSystemArtifactRecorder(db),
                new ArtifactRepository(db));

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
        var artifactPaths = await verifyDb.Artifacts.Select(a => a.Path).ToListAsync();
        var (structured, adapterKey, exitReason) = SplitPhaseCMetadata(persisted.StructuredResult);

        return new PathObservation(
            ExecutorRequests: Normalize(executor.Requests),
            ExceptionType: exceptionType,
            ExceptionMessage: exceptionMessage,
            Result: result is null
                ? null
                : new ResultSnapshot(
                    result.Executed, result.Success, result.FinalStatus, result.Output, result.ErrorMessage,
                    result.FailureCategory, string.Join(",", result.ToolCallNames),
                    string.Join(",", result.AvailableToolNames), result.CorrelationId),
            DurationMs: scenario == Scenario.CancelledMidFlight ? null : result?.DurationMs,
            PersistedStatus: persisted.Status,
            PersistedRun: new PersistedRunSnapshot(
                persisted.Output, persisted.ErrorMessage, persisted.FailureCategory, persisted.Provider, persisted.Model,
                persisted.PromptTokens, persisted.CompletionTokens, persisted.TotalTokens, persisted.Attempt,
                persisted.StructuredResultVersion, persisted.StartedAtUtc is not null, persisted.CompletedAtUtc is not null),
            StructuredResultJson: structured,
            ExecutionAdapter: adapterKey,
            ExitReason: exitReason,
            ArtifactPaths: artifactPaths.Select(NormalizePath).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Removes the two Phase C metadata keys (returning their values) and applies
    /// the same artifact-id normalization the baseline was recorded with.
    /// </summary>
    private static (string? Json, string? ExecutionAdapter, string? ExitReason) SplitPhaseCMetadata(string? json)
    {
        if (json is null)
        {
            return (null, null, null);
        }

        var parsed = JsonSerializer.Deserialize<StructuredAgentResult>(json, StructuredAgentResult.JsonOptions)!;
        string? adapterKey = null;
        string? exitReason = null;
        Dictionary<string, string>? metadata = null;

        if (parsed.Metadata is not null)
        {
            metadata = new Dictionary<string, string>();
            foreach (var (key, value) in parsed.Metadata)
            {
                switch (key)
                {
                    case "ExecutionAdapter":
                        adapterKey = value;
                        break;
                    case "ExitReason":
                        exitReason = value;
                        break;
                    // Phase J routing-observability additions (asserted in GatewayFailureAgentRunEndToEndTests).
                    case "Gateway":
                    case "GatewayFailure":
                        break;
                    default:
                        metadata[key] = value;
                        break;
                }
            }
        }

        var normalized = parsed with
        {
            Artifacts = parsed.Artifacts.Select((a, i) => Guid.TryParse(a, out _) ? $"artifact-id-{i}" : a).ToArray(),
            Metadata = metadata
        };

        return (JsonSerializer.Serialize(normalized, StructuredAgentResult.JsonOptions), adapterKey, exitReason);
    }

    private string NormalizePath(string path) => path.Replace(_workspace, "<workspace>").Replace('\\', '/');

    private string Normalize(IEnumerable<ExecutorRequest> requests) => JsonSerializer.Serialize(requests.Select(r => r with
    {
        WorkingDirectory = NormalizePath(r.WorkingDirectory),
        Prompt = r.Prompt.Replace(_workspace, "<workspace>").Replace("\r\n", "\n")
    }));

    private static IReadOnlyList<BaselineEntry> LoadBaseline()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate AI.Factory.slnx above the test assembly.");
        }

        var path = Path.Combine(
            dir.FullName, "tests", "AI.Factory.Application.Tests", "Execution", "Golden", "phase-b-baseline.json");

        return JsonSerializer.Deserialize<List<BaselineEntry>>(File.ReadAllText(path), BaselineJson)!;
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
