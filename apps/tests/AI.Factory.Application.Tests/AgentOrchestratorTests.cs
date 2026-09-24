using System.Text.Json;
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
/// Phase 18.1.5 — proves the complete Factory-owned orchestration path
/// (AgentRun -> AgentOrchestrator -> IAgentRuntime/IAgentToolRegistry/IAgentMemory
/// -> persisted AgentRun) without requiring real Hermes: FakeAgentRuntime
/// stands in for IAgentRuntime, real AgentRunRepository/AgentDefinitionRepository
/// against in-memory SQLite for persistence, matching AgentRunRepositoryTests'
/// established pattern.
/// </summary>
public sealed class AgentOrchestratorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public AgentOrchestratorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<AgentDefinition> SeedAgentDefinitionAsync()
    {
        var definition = new AgentDefinition
        {
            Name = "Coder",
            Description = "Implements WorkItems",
            AgentType = "Coding",
            SystemPrompt = "You are a coding agent."
        };

        await using var db = new FactoryDbContext(_options);
        return await new AgentDefinitionRepository(db).CreateAsync(definition);
    }

    private async Task<AgentRun> SeedAgentRunAsync(
        Guid agentDefinitionId,
        AgentRunStatus status,
        Guid? projectId = null,
        string? input = "do the thing",
        Guid? workItemId = null)
    {
        var run = new AgentRun
        {
            AgentDefinitionId = agentDefinitionId,
            ProjectId = projectId,
            WorkItemId = workItemId,
            Input = input
        };

        await using (var db = new FactoryDbContext(_options))
        {
            run = await new AgentRunRepository(db).CreateAsync(run);
        }

        if (status == AgentRunStatus.Created)
        {
            return run;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new AgentRunRepository(db);
            var reloaded = (await repo.GetByIdAsync(run.Id))!;

            foreach (var step in TransitionPath(status))
            {
                reloaded.TransitionTo(step);
            }

            await repo.UpdateAsync(reloaded);
            return reloaded;
        }
    }

    private async Task<Project> SeedProjectAsync(string rootPath)
    {
        var project = new Project
        {
            Name = "Workspace Test Project",
            Slug = $"workspace-test-{Guid.NewGuid():N}",
            RootPath = rootPath
        };

        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        await repo.AddAsync(project);
        await repo.SaveChangesAsync();
        return project;
    }

    private static IEnumerable<AgentRunStatus> TransitionPath(AgentRunStatus target) => target switch
    {
        AgentRunStatus.Queued => [AgentRunStatus.Queued],
        AgentRunStatus.Running => [AgentRunStatus.Queued, AgentRunStatus.Running],
        AgentRunStatus.Succeeded => [AgentRunStatus.Queued, AgentRunStatus.Running, AgentRunStatus.Succeeded],
        AgentRunStatus.Failed => [AgentRunStatus.Queued, AgentRunStatus.Running, AgentRunStatus.Failed],
        AgentRunStatus.Waiting => [AgentRunStatus.Queued, AgentRunStatus.Running, AgentRunStatus.Waiting],
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private static AgentOrchestrator CreateOrchestrator(
        FactoryDbContext db,
        IAgentRuntime runtime,
        FakeAgentToolRegistry? tools = null,
        FakeAgentMemory? memory = null,
        IWorkspaceResolver? workspaceResolver = null)
    {
        return new AgentOrchestrator(
            new AgentRunRepository(db),
            new ProjectRepository(db),
            tools ?? new FakeAgentToolRegistry("ReadArtifact"),
            memory ?? new FakeAgentMemory(),
            runtime,
            workspaceResolver ?? new ProjectWorkspaceResolver(new AgentWorkspaceOptions()),
            new AgentInstructionBuilder(),
            new StructuredAgentResultParser(),
            NullLogger<AgentOrchestrator>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ValidQueuedRun_TransitionsToSucceeded_AndPersists()
    {
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);

        Assert.True(result.Executed);
        Assert.True(result.Success);
        Assert.Equal(AgentRunStatus.Succeeded, result.FinalStatus);
        Assert.Equal(1, runtime.CallCount);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);
        Assert.Equal(AgentRunStatus.Succeeded, reloaded!.Status);
        Assert.Equal("fake output", reloaded.Output);
    }

    [Fact]
    public async Task ExecuteAsync_LoadsAgentDefinition_BeforeInvokingRuntime()
    {
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        await orchestrator.ExecuteAsync(run.Id);

        Assert.NotNull(runtime.LastAgentRun!.AgentDefinition);
        Assert.Equal(definition.Id, runtime.LastAgentRun.AgentDefinition!.Id);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesToolsThroughRegistry_AndReturnsDeterministicNames()
    {
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);
        var tools = new FakeAgentToolRegistry("ReadArtifact", "AnotherTool");

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime, tools: tools);

        var result = await orchestrator.ExecuteAsync(run.Id);

        Assert.Equal(new[] { "AnotherTool", "ReadArtifact" }, result.AvailableToolNames);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesMemoryThroughIAgentMemory_ScopedToProjectAndDefinition()
    {
        var definition = await SeedAgentDefinitionAsync();
        var projectId = Guid.NewGuid();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued, projectId: projectId);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);
        var memory = new FakeAgentMemory();

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime, memory: memory);

        await orchestrator.ExecuteAsync(run.Id);

        Assert.NotNull(memory.LastQuery);
        Assert.Equal(projectId, memory.LastQuery!.ProjectId);
        Assert.Equal(definition.Id, memory.LastQuery.AgentDefinitionId);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeFailure_TransitionsToFailed()
    {
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Failure);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);

        Assert.True(result.Executed);
        Assert.False(result.Success);
        Assert.Equal(AgentRunStatus.Failed, result.FinalStatus);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);
        Assert.Equal(AgentRunStatus.Failed, reloaded!.Status);
        Assert.Equal("Simulated agent failure.", reloaded.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeTimeout_TransitionsToTimedOut()
    {
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Timeout);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);

        Assert.Equal(AgentRunStatus.TimedOut, result.FinalStatus);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);
        Assert.Equal(AgentRunStatus.TimedOut, reloaded!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeThrowsCancellation_PropagatesException_LeavingRunRunning()
    {
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Cancellation);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        await Assert.ThrowsAsync<OperationCanceledException>(() => orchestrator.ExecuteAsync(run.Id));

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);
        Assert.Equal(AgentRunStatus.Running, reloaded!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeCancelledMidFlight_StillPersistsCancelled_DespiteTokenAlreadyCancelled()
    {
        // Regression (Phase 18.1.7 real Hermes E2E): a Worker execution
        // timeout cancels the SAME token the orchestrator later reused for
        // its own persistence write — the classified Cancelled result was
        // computed correctly but never saved, leaving the AgentRun stuck at
        // Running forever. The Hermes adapter never throws on cancellation —
        // it always returns a classified Cancelled result instead — so this
        // must persist even though the token is already cancelled by the
        // time that result comes back (matching real Worker timing: the
        // token cancels DURING the runtime call, not before ExecuteAsync
        // even starts).
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);

        using var cts = new CancellationTokenSource();
        var runtime = new CancelDuringExecutionRuntime(cts);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id, cts.Token);

        Assert.Equal(AgentRunStatus.Cancelled, result.FinalStatus);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);
        Assert.Equal(AgentRunStatus.Cancelled, reloaded!.Status);
    }

    /// <summary>
    /// Cancels the caller's token the moment the runtime is invoked, then
    /// returns a classified Cancelled result without throwing — exactly
    /// the Hermes execution path's real behavior when a Worker execution timeout
    /// fires mid-call, unlike FakeAgentRuntime.Mode.Cancellation (which
    /// models a runtime that throws instead).
    /// </summary>
    private sealed class CancelDuringExecutionRuntime : IAgentRuntime
    {
        private readonly CancellationTokenSource _cts;

        public CancelDuringExecutionRuntime(CancellationTokenSource cts) => _cts = cts;

        public Task<AgentRunExecutionResult> ExecuteAsync(
            AgentRun agentRun, CancellationToken cancellationToken = default)
        {
            _cts.Cancel();

            return Task.FromResult(new AgentRunExecutionResult(
                Success: false,
                Output: null,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                DurationMs: 5,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                ErrorMessage: "Agent execution was cancelled.",
                FailureCategory: FailureCategory.Cancelled,
                Provider: agentRun.Provider,
                Model: agentRun.Model));
        }
    }

    [Fact]
    public async Task ExecuteAsync_InvalidStartingState_IsRejected_WithoutInvokingRuntime()
    {
        var definition = await SeedAgentDefinitionAsync();
        // Created never transitioned to Queued — Created cannot execute directly.
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Created);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);

        Assert.False(result.Executed);
        Assert.Equal(0, runtime.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadySucceededRun_IsNotExecutedAgain()
    {
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Succeeded);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);

        Assert.False(result.Executed);
        Assert.Equal(0, runtime.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_RunningRun_ProceedsToExecution_ThisIsTheWorkerHandoffPath()
    {
        // A Running AgentRun is the expected input shape once IAgentRunRepository
        // .TryClaimNextPendingAsync (the Worker's atomic claim) has already run —
        // the orchestrator must execute it, not skip it as "someone else's."
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Running);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);

        Assert.True(result.Executed);
        Assert.True(result.Success);
        Assert.Equal(1, runtime.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAgentRunId_ReturnsNotExecuted()
    {
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(Guid.NewGuid());

        Assert.False(result.Executed);
        Assert.False(result.Success);
    }

    // --- Phase 18.1.8: structured agent result contract -------------------

    [Fact]
    public async Task ExecuteAsync_RuntimeReturnsValidStructuredJson_PersistsStructuredResult()
    {
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);
        var runtime = new FakeAgentRuntime(
            FakeAgentRuntime.Mode.Success,
            successOutput: """{"version":1,"status":"Succeeded","summary":"did it","output":"the answer"}""");

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);
        Assert.True(result.Success);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);

        Assert.Equal(AgentRunStatus.Succeeded, reloaded!.Status);
        Assert.Equal(1, reloaded.StructuredResultVersion);
        Assert.NotNull(reloaded.StructuredResult);

        var structured = JsonSerializer.Deserialize<StructuredAgentResult>(
            reloaded.StructuredResult!, StructuredAgentResult.JsonOptions);
        Assert.Equal(StructuredResultStatus.Succeeded, structured!.Status);
        Assert.Equal("did it", structured.Summary);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeReturnsMalformedOutput_PersistsInvalidStructuredResult_ButAgentRunStillSucceeds()
    {
        // The real runtime call succeeded (real exit code 0) — a malformed
        // self-report must never be silently accepted, but it also must
        // never flip AgentRunStatus away from what the runtime reported
        // (Phase 18.1.8 §4/§5: preserve existing AgentRun status semantics).
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);
        var runtime = new FakeAgentRuntime(
            FakeAgentRuntime.Mode.Success,
            successOutput: "I finished the task, everything looks good!");

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);
        Assert.True(result.Success);
        Assert.Equal(AgentRunStatus.Succeeded, result.FinalStatus);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);

        Assert.Equal(AgentRunStatus.Succeeded, reloaded!.Status);
        Assert.NotNull(reloaded.StructuredResult);

        var structured = JsonSerializer.Deserialize<StructuredAgentResult>(
            reloaded.StructuredResult!, StructuredAgentResult.JsonOptions);
        Assert.Equal(StructuredResultStatus.Invalid, structured!.Status);
        Assert.NotEmpty(structured.Warnings);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeFails_PersistsDeterministicFailedStructuredResult_WithoutParsingOutput()
    {
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Failure);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        await orchestrator.ExecuteAsync(run.Id);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);

        Assert.Equal(AgentRunStatus.Failed, reloaded!.Status);
        var structured = JsonSerializer.Deserialize<StructuredAgentResult>(
            reloaded.StructuredResult!, StructuredAgentResult.JsonOptions);
        Assert.Equal(StructuredResultStatus.Failed, structured!.Status);
        Assert.Contains("Simulated agent failure.", structured.Errors);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeTimesOut_PersistsDeterministicTimedOutStructuredResult()
    {
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Timeout);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        await orchestrator.ExecuteAsync(run.Id);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);

        Assert.Equal(AgentRunStatus.TimedOut, reloaded!.Status);
        var structured = JsonSerializer.Deserialize<StructuredAgentResult>(
            reloaded.StructuredResult!, StructuredAgentResult.JsonOptions);
        Assert.Equal(StructuredResultStatus.TimedOut, structured!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_BuiltInstruction_IncludesObjectiveAndStructuredOutputContract()
    {
        // Proves the orchestrator routes through IAgentInstructionBuilder
        // (Phase 18.1.8 §6) rather than concatenating prompt text itself —
        // the runtime must receive the full instruction text, not the raw
        // AgentRun.Input alone.
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(definition.Id, AgentRunStatus.Queued, input: "summarize this");
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        await orchestrator.ExecuteAsync(run.Id);

        Assert.NotNull(runtime.LastAgentRun);
        Assert.Contains("summarize this", runtime.LastAgentRun!.Input);
        Assert.Contains("\"version\":1", runtime.LastAgentRun.Input);
    }

    // --- Phase 18.14: deterministic agent workspace & execution context ---

    [Fact]
    public async Task ExecuteAsync_WorkItemBackedRun_WithValidProject_ResolvesWorkspace_AndRuntimeReceivesIt()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), "ai-factory-orchestrator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);

        try
        {
            var project = await SeedProjectAsync(workspaceDir);
            var definition = await SeedAgentDefinitionAsync();
            var run = await SeedAgentRunAsync(
                definition.Id, AgentRunStatus.Queued, projectId: project.Id, workItemId: Guid.NewGuid());
            var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

            await using var db = new FactoryDbContext(_options);
            var orchestrator = CreateOrchestrator(db, runtime);

            var result = await orchestrator.ExecuteAsync(run.Id);

            Assert.True(result.Executed);
            Assert.True(result.Success);
            Assert.Equal(1, runtime.CallCount);
            Assert.Equal(Path.GetFullPath(workspaceDir), runtime.LastAgentRun!.WorkspacePath);
        }
        finally
        {
            Directory.Delete(workspaceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WorkItemBackedRun_WithInvalidProjectWorkspace_FailsDeterministically_WithoutInvokingRuntime()
    {
        var nonexistentRoot = Path.Combine(Path.GetTempPath(), "ai-factory-orchestrator-tests", $"missing-{Guid.NewGuid():N}");
        var project = await SeedProjectAsync(nonexistentRoot);
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(
            definition.Id, AgentRunStatus.Queued, projectId: project.Id, workItemId: Guid.NewGuid());
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);

        Assert.True(result.Executed);
        Assert.False(result.Success);
        Assert.Equal(AgentRunStatus.Failed, result.FinalStatus);
        Assert.Equal(0, runtime.CallCount);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);
        Assert.Equal(AgentRunStatus.Failed, reloaded!.Status);
        Assert.Equal(FailureCategory.Validation, reloaded.FailureCategory);
    }

    [Fact]
    public async Task ExecuteAsync_WorkItemBackedRun_WithNoProjectId_FailsDeterministically_WithoutInvokingRuntime()
    {
        var definition = await SeedAgentDefinitionAsync();
        // WorkItemId set but ProjectId left null — the defensive case that
        // should never occur via TaskDispatchScheduler (it always sets both),
        // but must still fail safely rather than crash or silently skip
        // resolution.
        var run = await SeedAgentRunAsync(
            definition.Id, AgentRunStatus.Queued, projectId: null, workItemId: Guid.NewGuid());
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);

        Assert.False(result.Success);
        Assert.Equal(AgentRunStatus.Failed, result.FinalStatus);
        Assert.Equal(0, runtime.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_PlannerOnlyRun_WithoutWorkItemId_DoesNotRequireWorkspace_AndStillExecutes()
    {
        // Planner-only AgentRuns (WorkItemId == null) are explicitly out of
        // Phase 18.14's scope — this proves that behavior is untouched even
        // though a ProjectId is present (which, for a WorkItem-backed run,
        // would otherwise trigger resolution).
        var project = await SeedProjectAsync(
            Path.Combine(Path.GetTempPath(), "ai-factory-orchestrator-tests-does-not-exist"));
        var definition = await SeedAgentDefinitionAsync();
        var run = await SeedAgentRunAsync(
            definition.Id, AgentRunStatus.Queued, projectId: project.Id, workItemId: null);
        var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);

        await using var db = new FactoryDbContext(_options);
        var orchestrator = CreateOrchestrator(db, runtime);

        var result = await orchestrator.ExecuteAsync(run.Id);

        Assert.True(result.Executed);
        Assert.True(result.Success);
        Assert.Equal(1, runtime.CallCount);
        Assert.Null(runtime.LastAgentRun!.WorkspacePath);
    }

    [Fact]
    public async Task ExecuteAsync_RecoveredAgentRun_ResolvesSameWorkspace_FromPersistedStateAlone()
    {
        // Simulates loading a WorkItem-backed AgentRun in two completely
        // separate AgentOrchestrator instances/scopes (as would happen across
        // a Worker restart) and proves workspace resolution depends only on
        // AgentRun.ProjectId -> Project.RootPath, never on in-memory state.
        var workspaceDir = Path.Combine(Path.GetTempPath(), "ai-factory-orchestrator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);

        try
        {
            var project = await SeedProjectAsync(workspaceDir);
            var definition = await SeedAgentDefinitionAsync();
            var run = await SeedAgentRunAsync(
                definition.Id, AgentRunStatus.Queued, projectId: project.Id, workItemId: Guid.NewGuid());

            var firstRuntime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);
            await using (var firstScopeDb = new FactoryDbContext(_options))
            {
                var firstOrchestrator = CreateOrchestrator(firstScopeDb, firstRuntime);
                await firstOrchestrator.ExecuteAsync(run.Id);
            }

            // A second AgentRun against the same Project, resolved by a brand
            // new AgentOrchestrator instance/DbContext — nothing shared with
            // the first except persisted state.
            var secondRun = await SeedAgentRunAsync(
                definition.Id, AgentRunStatus.Queued, projectId: project.Id, workItemId: Guid.NewGuid());
            var secondRuntime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success);
            await using (var secondScopeDb = new FactoryDbContext(_options))
            {
                var secondOrchestrator = CreateOrchestrator(secondScopeDb, secondRuntime);
                await secondOrchestrator.ExecuteAsync(secondRun.Id);
            }

            Assert.Equal(firstRuntime.LastAgentRun!.WorkspacePath, secondRuntime.LastAgentRun!.WorkspacePath);
            Assert.Equal(Path.GetFullPath(workspaceDir), secondRuntime.LastAgentRun.WorkspacePath);
        }
        finally
        {
            Directory.Delete(workspaceDir, recursive: true);
        }
    }
}
