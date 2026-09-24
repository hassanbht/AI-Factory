using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
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
/// Execution-adapter Phase C — workspace artifact recording moved from the Hermes
/// runtime into AgentOrchestrator. Same recorder (FileSystemArtifactRecorder, every
/// file), same condition (successful, WorkItem-backed run with a resolved
/// workspace), same ProjectId/WorkItemId, same cancellation token, same returned
/// references — only the call site moved. The runtime here is scripted, so the
/// assertions are about the Factory side alone.
/// </summary>
public sealed class AgentOrchestratorArtifactRecordingTests : IDisposable
{
    private const string AgentType = "Implementation";
    private const string Category = "coding";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly string _workspace;

    public AgentOrchestratorArtifactRecordingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using (var db = new FactoryDbContext(_options))
        {
            db.Database.EnsureCreated();
        }

        _workspace = Directory.CreateTempSubdirectory("orchestrator-artifacts-").FullName;
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        File.WriteAllText(Path.Combine(_workspace, "src", "Changed.cs"), "// changed");
        File.WriteAllText(Path.Combine(_workspace, "README.md"), "readme");
        Directory.CreateDirectory(Path.Combine(_workspace, "bin"));
        File.WriteAllText(Path.Combine(_workspace, "bin", "excluded.dll"), "x");
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class ScriptedRuntime : IAgentRuntime
    {
        private readonly Queue<Func<AgentRun, CancellationToken, AgentRunExecutionResult>> _steps;

        public ScriptedRuntime(params Func<AgentRun, CancellationToken, AgentRunExecutionResult>[] steps) =>
            _steps = new Queue<Func<AgentRun, CancellationToken, AgentRunExecutionResult>>(steps);

        public List<AgentRun> Snapshots { get; } = new();

        public Task<AgentRunExecutionResult> ExecuteAsync(AgentRun agentRun, CancellationToken cancellationToken = default)
        {
            Snapshots.Add(agentRun);
            return Task.FromResult(_steps.Dequeue()(agentRun, cancellationToken));
        }
    }

    /// <summary>The real recorder, with every call (and its token) captured.</summary>
    private sealed class RecordingRecorder : IArtifactRecorder
    {
        private readonly IArtifactRecorder _inner;
        private readonly Exception? _throw;

        public RecordingRecorder(IArtifactRecorder inner, Exception? throwOnRecord = null)
        {
            _inner = inner;
            _throw = throwOnRecord;
        }

        public List<(Guid ProjectId, Guid WorkItemId, string Workspace, CancellationToken Token)> Calls { get; } = new();

        public Task RecordWorkspaceArtifactsAsync(
            Guid projectId, Guid workItemId, string workspacePath, CancellationToken cancellationToken = default)
        {
            Calls.Add((projectId, workItemId, workspacePath, cancellationToken));
            return _throw is null
                ? _inner.RecordWorkspaceArtifactsAsync(projectId, workItemId, workspacePath, cancellationToken)
                : throw _throw;
        }
    }

    private static AgentRunExecutionResult Result(
        bool success, FailureCategory? category = null, string? output = "not structured json") => new(
        Success: success,
        Output: output,
        PromptTokens: null,
        CompletionTokens: null,
        TotalTokens: null,
        DurationMs: 7,
        ArtifactReferences: Array.Empty<string>(),
        ToolCallNames: Array.Empty<string>(),
        ErrorMessage: success ? null : "went wrong",
        FailureCategory: category,
        Provider: null,
        Model: null,
        ExecutionAdapter: "Hermes",
        ExitReason: success ? "exit:0" : "exit:1");

    private async Task<(Guid RunId, Guid ProjectId, Guid? WorkItemId)> SeedAsync(bool workItemBacked = true)
    {
        await using var db = new FactoryDbContext(_options);

        var definition = await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = "Coder",
            Description = "Implements WorkItems",
            AgentType = AgentType,
            SystemPrompt = "You are a coding agent."
        });

        var project = new Project { Name = "Artifacts", Slug = "artifacts-" + Guid.NewGuid().ToString("N"), RootPath = _workspace };
        var projects = new ProjectRepository(db);
        await projects.AddAsync(project);
        await projects.SaveChangesAsync();

        var runs = new AgentRunRepository(db);
        var run = await runs.CreateAsync(new AgentRun
        {
            AgentDefinitionId = definition.Id,
            ProjectId = project.Id,
            WorkItemId = workItemBacked ? Guid.NewGuid() : null,
            Input = "Implement it."
        });
        run.TransitionTo(AgentRunStatus.Queued);
        await runs.UpdateAsync(run);

        return (run.Id, project.Id, run.WorkItemId);
    }

    private static ModelRoutingOptions TwoCandidateRouting()
    {
        var options = new ModelRoutingOptions
        {
            Models =
            {
                new ModelCapability
                {
                    ModelId = "primary", Provider = "gw", ReasoningStrength = 4,
                    MaxContextTokens = 200_000, CostTier = 1, IsAvailable = true
                },
                new ModelCapability
                {
                    ModelId = "fallback", Provider = "gw", ReasoningStrength = 4,
                    MaxContextTokens = 200_000, CostTier = 2, IsAvailable = true
                }
            }
        };
        options.CategoryProfiles[Category] = new List<string> { "primary", "fallback" };
        options.AgentTypeCategories[AgentType] = Category;
        return options;
    }

    private async Task<(AgentOrchestrationResult? Result, Exception? Error, RecordingRecorder Recorder)> ExecuteAsync(
        Guid runId,
        IAgentRuntime runtime,
        CancellationToken cancellationToken = default,
        ModelRoutingOptions? routing = null,
        Exception? recorderThrows = null)
    {
        await using var db = new FactoryDbContext(_options);
        var recorder = new RecordingRecorder(new FileSystemArtifactRecorder(db), recorderThrows);

        var orchestrator = new AgentOrchestrator(
            new AgentRunRepository(db),
            new ProjectRepository(db),
            new FakeAgentToolRegistry(),
            new FakeAgentMemory(),
            runtime,
            new ProjectWorkspaceResolver(new AgentWorkspaceOptions()),
            new AgentInstructionBuilder(),
            new StructuredAgentResultParser(),
            NullLogger<AgentOrchestrator>.Instance,
            routing is null ? null : new ModelRouter(routing),
            routing,
            recorder,
            new ArtifactRepository(db));

        try
        {
            return (await orchestrator.ExecuteAsync(runId, cancellationToken), null, recorder);
        }
        catch (Exception ex)
        {
            return (null, ex, recorder);
        }
    }

    private async Task<(AgentRun Run, List<Artifact> Artifacts, StructuredAgentResult? Structured)> LoadAsync(Guid runId)
    {
        await using var db = new FactoryDbContext(_options);
        var run = (await new AgentRunRepository(db).GetByIdAsync(runId))!;
        var artifacts = await db.Artifacts.ToListAsync();
        var structured = run.StructuredResult is null
            ? null
            : JsonSerializer.Deserialize<StructuredAgentResult>(run.StructuredResult, StructuredAgentResult.JsonOptions);
        return (run, artifacts, structured);
    }

    [Fact]
    public async Task SuccessfulWorkItemRun_RecordsEveryWorkspaceFile_WithItsProjectWorkItemWorkspaceAndToken()
    {
        var (runId, projectId, workItemId) = await SeedAsync();
        var runtime = new ScriptedRuntime((_, _) => Result(success: true));
        using var cts = new CancellationTokenSource();

        var (result, error, recorder) = await ExecuteAsync(runId, runtime, cts.Token);

        Assert.Null(error);
        Assert.True(result!.Success);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal(projectId, call.ProjectId);
        Assert.Equal(workItemId, call.WorkItemId);
        // Exactly the workspace the runtime executed in.
        Assert.Equal(Assert.Single(runtime.Snapshots).WorkspacePath, call.Workspace);
        Assert.Equal(Path.GetFullPath(_workspace).TrimEnd(Path.DirectorySeparatorChar), call.Workspace.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(cts.Token, call.Token);

        var (run, artifacts, structured) = await LoadAsync(runId);
        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.Equal(
            new[] { "Changed.cs", "README.md" },
            artifacts.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(artifacts, a => Assert.Equal(projectId, a.ProjectId));
        Assert.All(artifacts, a => Assert.Equal(workItemId, a.WorkItemId));

        // Unparseable output -> the recorded artifact ids surface in the structured result, as before.
        Assert.Equal(
            artifacts.Select(a => a.Id.ToString()).OrderBy(x => x, StringComparer.Ordinal),
            structured!.Artifacts.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(FailureCategory.Agent, AgentRunStatus.Failed)]
    [InlineData(FailureCategory.Timeout, AgentRunStatus.TimedOut)]
    [InlineData(FailureCategory.Cancelled, AgentRunStatus.Cancelled)]
    [InlineData(FailureCategory.Validation, AgentRunStatus.Failed)]
    public async Task UnsuccessfulRun_RecordsNoArtifacts(FailureCategory category, AgentRunStatus expected)
    {
        var (runId, _, _) = await SeedAsync();

        var (_, error, recorder) = await ExecuteAsync(runId, new ScriptedRuntime((_, _) => Result(false, category)));

        Assert.Null(error);
        Assert.Empty(recorder.Calls);
        var (run, artifacts, structured) = await LoadAsync(runId);
        Assert.Equal(expected, run.Status);
        Assert.Empty(artifacts);
        Assert.Empty(structured!.Artifacts);
    }

    [Fact]
    public async Task RunWithoutWorkItem_RecordsNoArtifacts()
    {
        var (runId, _, _) = await SeedAsync(workItemBacked: false);

        var (result, _, recorder) = await ExecuteAsync(runId, new ScriptedRuntime((_, _) => Result(true)));

        Assert.True(result!.Success);
        Assert.Empty(recorder.Calls);
        Assert.Empty((await LoadAsync(runId)).Artifacts);
    }

    [Fact]
    public async Task RuntimeException_RecordsNoArtifacts_AndPropagates()
    {
        var (runId, _, _) = await SeedAsync();

        var (_, error, recorder) = await ExecuteAsync(
            runId, new ScriptedRuntime((_, _) => throw new InvalidOperationException("runtime exploded")));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Empty(recorder.Calls);
        Assert.Equal(AgentRunStatus.Running, (await LoadAsync(runId)).Run.Status);
    }

    [Fact]
    public async Task RecordingCancelledAfterASuccessfulExecution_Propagates_LeavingTheRunForRecovery()
    {
        // Same as when the recorder ran inside the runtime: the cancellation
        // escapes before any terminal status is decided.
        var (runId, _, _) = await SeedAsync();

        var (_, error, recorder) = await ExecuteAsync(
            runId, new ScriptedRuntime((_, _) => Result(true)), recorderThrows: new OperationCanceledException());

        Assert.IsType<OperationCanceledException>(error);
        Assert.Single(recorder.Calls);
        Assert.Equal(AgentRunStatus.Running, (await LoadAsync(runId)).Run.Status);
    }

    [Fact]
    public async Task FallbackAfterAProviderFailure_RecordsOnlyForTheSuccessfulFinalExecution()
    {
        var (runId, _, _) = await SeedAsync();
        var runtime = new ScriptedRuntime(
            (_, _) => Result(false, FailureCategory.Provider),
            (_, _) => Result(true));

        var (result, _, recorder) = await ExecuteAsync(runId, runtime, routing: TwoCandidateRouting());

        Assert.True(result!.Success);
        Assert.Equal(2, runtime.Snapshots.Count);
        Assert.Single(recorder.Calls);
        Assert.Equal(2, (await LoadAsync(runId)).Artifacts.Count);
    }

    [Fact]
    public async Task ArtifactReferences_AreThisProjectsArtifactsForTheWorkItem()
    {
        var (runId, projectId, workItemId) = await SeedAsync();
        var foreign = Guid.NewGuid();
        await using (var db = new FactoryDbContext(_options))
        {
            var other = new Project { Name = "Other", Slug = "other-" + Guid.NewGuid().ToString("N"), RootPath = _workspace };
            db.Projects.Add(other);
            db.Artifacts.Add(new Artifact
            {
                Id = foreign,
                ProjectId = other.Id,
                WorkItemId = workItemId!.Value,
                Name = "foreign.txt",
                Path = "/elsewhere/foreign.txt",
                ArtifactType = "txt",
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await ExecuteAsync(runId, new ScriptedRuntime((_, _) => Result(true)));

        var (_, artifacts, structured) = await LoadAsync(runId);
        Assert.DoesNotContain(foreign.ToString(), structured!.Artifacts);
        Assert.Equal(artifacts.Count(a => a.ProjectId == projectId), structured.Artifacts.Count);
    }

    [Fact]
    public async Task WithoutArtifactDependencies_NothingIsRecorded_AndTheRuntimeReferencesAreKept()
    {
        var (runId, _, _) = await SeedAsync();
        await using var db = new FactoryDbContext(_options);

        var orchestrator = new AgentOrchestrator(
            new AgentRunRepository(db), new ProjectRepository(db), new FakeAgentToolRegistry(), new FakeAgentMemory(),
            new ScriptedRuntime((_, _) => Result(true) with { ArtifactReferences = new[] { "runtime-ref" } }),
            new ProjectWorkspaceResolver(new AgentWorkspaceOptions()), new AgentInstructionBuilder(),
            new StructuredAgentResultParser(), NullLogger<AgentOrchestrator>.Instance);

        await orchestrator.ExecuteAsync(runId);

        var (_, artifacts, structured) = await LoadAsync(runId);
        Assert.Empty(artifacts);
        Assert.Equal(new[] { "runtime-ref" }, structured!.Artifacts);
    }

    [Fact]
    public void ArtifactDependencies_MustBeSuppliedTogether()
    {
        using var db = new FactoryDbContext(_options);

        Assert.Throws<ArgumentException>(() => new AgentOrchestrator(
            new AgentRunRepository(db), new ProjectRepository(db), new FakeAgentToolRegistry(), new FakeAgentMemory(),
            new ScriptedRuntime(), new ProjectWorkspaceResolver(new AgentWorkspaceOptions()), new AgentInstructionBuilder(),
            new StructuredAgentResultParser(), NullLogger<AgentOrchestrator>.Instance,
            artifactRecorder: new FileSystemArtifactRecorder(db)));
    }

    [Fact]
    public async Task AdapterKeyAndExitReason_AreRecordedAsStructuredResultMetadata()
    {
        var (runId, _, _) = await SeedAsync();

        await ExecuteAsync(runId, new ScriptedRuntime((_, _) => Result(false, FailureCategory.Agent)));

        var metadata = (await LoadAsync(runId)).Structured!.Metadata!;
        Assert.Equal("Hermes", metadata["ExecutionAdapter"]);
        Assert.Equal("exit:1", metadata["ExitReason"]);
    }

    [Fact]
    public async Task NoAdapterReported_AddsNoAdapterMetadata()
    {
        var (runId, _, _) = await SeedAsync();

        await ExecuteAsync(
            runId, new ScriptedRuntime((_, _) => Result(false, FailureCategory.Validation) with { ExecutionAdapter = null, ExitReason = null }));

        var metadata = (await LoadAsync(runId)).Structured!.Metadata!;
        Assert.False(metadata.ContainsKey("ExecutionAdapter"));
        Assert.False(metadata.ContainsKey("ExitReason"));
    }
}
