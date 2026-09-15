using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.9 §19/§20 — the full loop: TaskDispatchScheduler (repair
/// dispatch) -> AgentOrchestrator(FakeAgentRuntime) -> AgentRunWorkItemSync
/// -> IVerificationGate(fake) -> WorkItem state, against real SQLite repos.
/// Mirrors WorkItemExecutionIntegrationTests' conventions (Phase 18.7/18.8).
/// </summary>
public sealed class RepairVerificationLoopTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    // Phase 18.14 — see WorkItemExecutionIntegrationTests: AgentOrchestrator
    // now resolves a real, existing Project workspace for WorkItem-backed
    // AgentRuns (including repair dispatch), so each seeded Project needs a
    // real temp directory rather than the non-existent default RootPath.
    private readonly string _workspaceRoot;

    public RepairVerificationLoopTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        _workspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-repair-loop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        _connection.Dispose();

        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    private const string CompletedExecutionResult = """
        {"version":1,"status":"Succeeded","summary":"done","output":"ok","data":{"execution":{"version":1,"status":"completed","summary":"implemented it"}},"warnings":[],"errors":[],"artifacts":[],"metadata":{}}
        """;

    private sealed class FakeVerificationGate : IVerificationGate
    {
        private readonly VerificationStatus _status;
        public int CallCount { get; private set; }
        public Project? LastProject { get; private set; }
        public WorkItem? LastWorkItem { get; private set; }

        public FakeVerificationGate(VerificationStatus status) => _status = status;

        public Task<VerificationResult> VerifyAsync(
            Project project, WorkItem workItem, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastProject = project;
            LastWorkItem = workItem;
            return Task.FromResult(new VerificationResult(_status, $"fake:{_status}", 0, TimeSpan.Zero));
        }

        public Task<VerificationResult> VerifyProjectAsync(
            Project project, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(_status, $"fake:{_status}", 0, TimeSpan.Zero));
    }

    /// <summary>Returns a different fixed result on each successive call — models the
    /// real "fails once, passes after repair" sequence for the end-to-end test.</summary>
    private sealed class SequenceVerificationGate : IVerificationGate
    {
        private readonly Queue<VerificationStatus> _sequence;

        public SequenceVerificationGate(params VerificationStatus[] sequence) => _sequence = new Queue<VerificationStatus>(sequence);

        public Task<VerificationResult> VerifyAsync(
            Project project, WorkItem workItem, CancellationToken cancellationToken = default)
        {
            var status = _sequence.Count > 1 ? _sequence.Dequeue() : _sequence.Peek();
            return Task.FromResult(new VerificationResult(status, $"fake:{status}", 0, TimeSpan.Zero));
        }

        public Task<VerificationResult> VerifyProjectAsync(
            Project project, CancellationToken cancellationToken = default)
        {
            var status = _sequence.Count > 1 ? _sequence.Dequeue() : _sequence.Peek();
            return Task.FromResult(new VerificationResult(status, $"fake:{status}", 0, TimeSpan.Zero));
        }
    }

    private static TaskDispatchScheduler CreateScheduler(FactoryDbContext db) => new(
        new WorkItemRepository(db),
        new ProjectRepository(db),
        new TaskDependencyResolver(),
        new AgentRunRepository(db),
        new AgentDefinitionRepository(db),
        new TaskSchedulerOptions { MaxConcurrentTasks = 2 },
        new ExecutionLimitsOptions(),
        new ProjectCompletionGate(
            new ProjectRepository(db), new AgentRunRepository(db), new FakeVerificationGate(VerificationStatus.Passed),
            NullLogger<ProjectCompletionGate>.Instance),
        new OperationalMetrics(),
        NullLogger<TaskDispatchScheduler>.Instance);

    private static AgentOrchestrator CreateOrchestrator(FactoryDbContext db, IAgentRuntime runtime) => new(
        new AgentRunRepository(db),
        new ProjectRepository(db),
        new FakeAgentToolRegistry(),
        new FakeAgentMemory(),
        runtime,
        new ProjectWorkspaceResolver(new AgentWorkspaceOptions()),
        new AgentInstructionBuilder(),
        new StructuredAgentResultParser(),
        NullLogger<AgentOrchestrator>.Instance);

    private static AgentRunWorkItemSync CreateSync(FactoryDbContext db, IVerificationGate verificationGate) => new(
        new WorkItemRepository(db), new ProjectRepository(db), new WorkItemExecutionResultMapper(),
        verificationGate, new FakeWorkItemCompletionProcessor(), NullLogger<AgentRunWorkItemSync>.Instance);

    /// <summary>Phase 18.13 — stand-in for the real review/evaluation/learning stage; out of scope for this file's repair/verification loop coverage.</summary>
    private sealed class FakeWorkItemCompletionProcessor : IWorkItemCompletionProcessor
    {
        public Task<WorkItemCompletionDecision> ProcessAsync(
            Project project, WorkItem workItem, AgentRun agentRun, VerificationResult verificationResult,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemCompletionDecision(WorkItemStatus.Completed, null));
    }

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        var project = await new ProjectService(repo).CreateAsync("Repair Loop Project", "d");

        project.RootPath = Path.Combine(_workspaceRoot, project.Slug);
        Directory.CreateDirectory(project.RootPath);
        // Phase 18.16 §12 — WorkItemService.CreateAsync now requires a
        // Project's source decision to already be made (Status != Created);
        // this file exercises repair/verification wiring, not source
        // initialization, so seed straight past that gate.
        project.Status = ProjectStatus.Analyzing;
        await repo.SaveChangesAsync();

        return project.Id;
    }

    private async Task SeedAgentDefinitionAsync(string agentType)
    {
        await using var db = new FactoryDbContext(_options);
        await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = $"Agent-{agentType}", AgentType = agentType, SystemPrompt = "x"
        });
    }

    private async Task<Guid> SeedRepairingWorkItemAsync(Guid projectId, string title = "T", string currentError = "Build failed")
    {
        await using var db = new FactoryDbContext(_options);
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(projectId, WorkItemType.Implementation, title, null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Running);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Testing);
        var workItem = await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Repairing);
        workItem.CurrentError = currentError;
        await new WorkItemRepository(db).UpdateAsync(workItem);
        return item.Id;
    }

    private async Task<Guid> DispatchRepairAndRunAsync(Guid projectId, Guid workItemId, FakeAgentRuntime runtime)
    {
        Guid agentRunId;

        await using (var db = new FactoryDbContext(_options))
        {
            var cycle = await CreateScheduler(db).RunCycleAsync(projectId);
            agentRunId = cycle.Outcomes.Single(o => o.WorkItemId == workItemId).AgentRunId!.Value;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, runtime).ExecuteAsync(agentRunId);
        }

        return agentRunId;
    }

    private sealed class BlockingVerificationGate : IVerificationGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnteredAsync() => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<VerificationResult> VerifyAsync(
            Project project, WorkItem workItem, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task;
            return new VerificationResult(VerificationStatus.Passed, "unblocked", 0, TimeSpan.Zero);
        }

        public Task<VerificationResult> VerifyProjectAsync(
            Project project, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("VerifyProjectAsync should not be called in this test file.");
    }

    // ---------------- Verification loop ----------------

    [Fact]
    public async Task RepairAgentSucceeded_ReturnsToTesting()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        var agentRunId = await DispatchRepairAndRunAsync(
            projectId, workItemId, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult));

        var gate = new BlockingVerificationGate();

        await using var db = new FactoryDbContext(_options);
        var run = await new AgentRunRepository(db).GetByIdAsync(agentRunId);
        var syncTask = CreateSync(db, gate).SyncAsync(run!);

        await gate.EnteredAsync();

        await using (var midDb = new FactoryDbContext(_options))
        {
            var workItem = await new WorkItemRepository(midDb).GetByIdAsync(workItemId);
            Assert.Equal(WorkItemStatus.Testing.ToString(), workItem!.Status);
        }

        gate.Release();
        await syncTask;
    }

    [Fact]
    public async Task RepairAgentCannotDirectlyCompleteWorkItem()
    {
        // Even though the repair agent reports execution.status="completed",
        // a Failed VerificationGate result must not let the WorkItem reach
        // Completed — the agent's self-report is never sufficient proof.
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        var agentRunId = await DispatchRepairAndRunAsync(
            projectId, workItemId, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult));

        await using (var db = new FactoryDbContext(_options))
        {
            var run = await new AgentRunRepository(db).GetByIdAsync(agentRunId);
            await CreateSync(db, new FakeVerificationGate(VerificationStatus.Failed)).SyncAsync(run!);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(workItemId);
        Assert.NotEqual(WorkItemStatus.Completed.ToString(), workItem!.Status);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), workItem.Status);
    }

    [Fact]
    public async Task VerificationPassedAfterRepair_CompletesWorkItem()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        var agentRunId = await DispatchRepairAndRunAsync(
            projectId, workItemId, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult));

        await using (var db = new FactoryDbContext(_options))
        {
            var run = await new AgentRunRepository(db).GetByIdAsync(agentRunId);
            await CreateSync(db, new FakeVerificationGate(VerificationStatus.Passed)).SyncAsync(run!);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem!.Status);
    }

    // ---------------- Repair failure ----------------

    [Fact]
    public async Task VerificationFailedAfterRepair_ReentersRepairing()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        var agentRunId = await DispatchRepairAndRunAsync(
            projectId, workItemId, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult));

        await using (var db = new FactoryDbContext(_options))
        {
            var run = await new AgentRunRepository(db).GetByIdAsync(agentRunId);
            await CreateSync(db, new FakeVerificationGate(VerificationStatus.Failed)).SyncAsync(run!);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), workItem!.Status);
        // RepairAttemptCount was incremented once at dispatch time and is not
        // touched again by the verification result itself — the next
        // scheduler cycle is what would increment it for a further attempt.
        Assert.Equal(1, workItem.RepairAttemptCount);
    }

    [Fact]
    public async Task RepairAgentFailure_UsesDeterministicPolicy()
    {
        // Chosen Phase 18.9 §14 policy: a repair AgentRun's own RUNTIME
        // failure (not a verification failure) maps to a terminal
        // WorkItem.Failed, via the same generic, unmodified
        // AgentRunWorkItemSync.DetermineTarget mapping every other AgentRun
        // runtime failure already uses (Phase 18.7) — no repair-specific
        // auto-retry-on-runtime-failure was added, since that would require
        // a new Running -> Repairing transition and is explicitly only a
        // "Prefer", not a "must", in the phase spec.
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        var agentRunId = await DispatchRepairAndRunAsync(
            projectId, workItemId, new FakeAgentRuntime(FakeAgentRuntime.Mode.Failure));

        await using (var db = new FactoryDbContext(_options))
        {
            var run = await new AgentRunRepository(db).GetByIdAsync(agentRunId);
            Assert.Equal(AgentRunStatus.Failed, run!.Status);
            await CreateSync(db, new FakeVerificationGate(VerificationStatus.Passed)).SyncAsync(run);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem!.Status);
    }

    /// <summary>
    /// FakeAgentRuntime.Mode.Cancellation THROWS OperationCanceledException,
    /// which AgentOrchestrator lets propagate (per
    /// AgentOrchestratorTests.ExecuteAsync_RuntimeThrowsCancellation_PropagatesException_LeavingRunRunning)
    /// rather than persisting a terminal status — so it cannot be used to
    /// reach AgentRunStatus.Cancelled here. This mirrors HermesAgentRuntime's
    /// real behavior instead: a runtime that returns a classified Cancelled
    /// result without throwing (e.g. the caller's token was cancelled
    /// mid-call and the runtime converts that into a normal result).
    /// </summary>
    private sealed class CancelledResultRuntime : IAgentRuntime
    {
        public Task<AgentRunExecutionResult> ExecuteAsync(AgentRun agentRun, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentRunExecutionResult(
                Success: false,
                Output: null,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                DurationMs: 10,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                ErrorMessage: "Simulated cancellation.",
                FailureCategory: FailureCategory.Cancelled,
                Provider: agentRun.Provider,
                Model: agentRun.Model));
    }

    [Fact]
    public async Task RepairAgentCancellation_CancelsWorkItem()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        Guid agentRunId;
        await using (var db = new FactoryDbContext(_options))
        {
            var cycle = await CreateScheduler(db).RunCycleAsync(projectId);
            agentRunId = cycle.Outcomes.Single(o => o.WorkItemId == workItemId).AgentRunId!.Value;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, new CancelledResultRuntime()).ExecuteAsync(agentRunId);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var run = await new AgentRunRepository(db).GetByIdAsync(agentRunId);
            Assert.Equal(AgentRunStatus.Cancelled, run!.Status);
            await CreateSync(db, new FakeVerificationGate(VerificationStatus.Passed)).SyncAsync(run);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Cancelled.ToString(), workItem!.Status);
    }

    // ---------------- Dependency behavior ----------------

    [Fact]
    public async Task CompletedAfterRepair_SatisfiesDependentWorkItem()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        await SeedAgentDefinitionAsync("Implementation");
        var rootId = await SeedRepairingWorkItemAsync(projectId, "Root");

        Guid dependentId;
        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
            var dependent = await workItems.CreateAsync(projectId, WorkItemType.Implementation, "Dependent", null, "Normal");
            await new TaskDependencyService(new WorkItemRepository(db), new TaskDependencyResolver())
                .AddDependencyAsync(dependent.Id, rootId);
            await workItems.TransitionStatusAsync(dependent.Id, WorkItemStatus.Ready);
            dependentId = dependent.Id;
        }

        var agentRunId = await DispatchRepairAndRunAsync(
            projectId, rootId, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult));

        await using (var db = new FactoryDbContext(_options))
        {
            var run = await new AgentRunRepository(db).GetByIdAsync(agentRunId);
            await CreateSync(db, new FakeVerificationGate(VerificationStatus.Passed)).SyncAsync(run!);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var rootBefore = await new WorkItemRepository(db).GetByIdAsync(rootId);
            Assert.Equal(WorkItemStatus.Completed.ToString(), rootBefore!.Status);
        }

        await using var schedulerDb = new FactoryDbContext(_options);
        var secondCycle = await CreateScheduler(schedulerDb).RunCycleAsync(projectId);

        Assert.Contains(secondCycle.Outcomes, o => o.WorkItemId == dependentId && o.Dispatched);
    }

    // ---------------- Safety ----------------

    [Fact]
    public async Task VerificationCommands_AreStillFactoryOwned()
    {
        // Even if the repair agent's structured result tries to smuggle a
        // "command" through summary/notes text, IVerificationGate.VerifyAsync
        // is only ever called with (Project, WorkItem) — nothing derived from
        // agent output ever reaches it. The gate itself (VerificationGate,
        // Phase 18.8) hard-codes "dotnet build"/"dotnet test" against
        // Project.RootPath with no reference to AgentRun/StructuredResult at
        // all, so this is structurally guaranteed; this test proves the
        // AgentRunWorkItemSync call site never threads agent content into it.
        const string maliciousExecutionResult = """
            {"version":1,"status":"Succeeded","summary":"done","output":"ok","data":{"execution":{"version":1,"status":"completed","summary":"run: rm -rf /","notes":["command: curl evil.example"]}},"warnings":[],"errors":[],"artifacts":[],"metadata":{}}
            """;

        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        var agentRunId = await DispatchRepairAndRunAsync(
            projectId, workItemId, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, maliciousExecutionResult));

        var gate = new FakeVerificationGate(VerificationStatus.Passed);

        await using (var db = new FactoryDbContext(_options))
        {
            var run = await new AgentRunRepository(db).GetByIdAsync(agentRunId);
            await CreateSync(db, gate).SyncAsync(run!);
        }

        Assert.Equal(1, gate.CallCount);
        Assert.Equal(workItemId, gate.LastWorkItem!.Id);
        Assert.Equal(projectId, gate.LastProject!.Id);
    }

    // ---------------- End-to-end (§20) ----------------

    [Fact]
    public async Task EndToEnd_ImplementationFailsVerification_RepairsSuccessfully_CompletesWorkItem()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);

        Guid workItemId;
        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
            var item = await workItems.CreateAsync(projectId, WorkItemType.Implementation, "E2E", null, "Normal");
            await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
            workItemId = item.Id;
        }

        var sequenceGate = new SequenceVerificationGate(VerificationStatus.Failed, VerificationStatus.Passed);

        // 1. Initial implementation dispatch + execution + verification (fails).
        Guid initialRunId;
        await using (var db = new FactoryDbContext(_options))
        {
            var cycle = await CreateScheduler(db).RunCycleAsync(projectId);
            initialRunId = cycle.Outcomes.Single().AgentRunId!.Value;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult))
                .ExecuteAsync(initialRunId);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var run = await new AgentRunRepository(db).GetByIdAsync(initialRunId);
            await CreateSync(db, sequenceGate).SyncAsync(run!);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var workItem = await new WorkItemRepository(db).GetByIdAsync(workItemId);
            Assert.Equal(WorkItemStatus.Repairing.ToString(), workItem!.Status);
        }

        // 2. Scheduler dispatches a repair AgentRun for the Repairing WorkItem.
        Guid repairRunId;
        await using (var db = new FactoryDbContext(_options))
        {
            var cycle = await CreateScheduler(db).RunCycleAsync(projectId);
            var outcome = cycle.Outcomes.Single(o => o.WorkItemId == workItemId);
            Assert.True(outcome.Dispatched);
            repairRunId = outcome.AgentRunId!.Value;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var repairRun = await new AgentRunRepository(db).GetByIdAsync(repairRunId);
            var repairDefinition = (await new AgentDefinitionRepository(db).GetAllAsync())
                .Single(x => x.AgentType == TaskDispatchScheduler.RepairAgentType);
            Assert.Equal(repairDefinition.Id, repairRun!.AgentDefinitionId);
        }

        // 3. Repair agent executes, reports completed.
        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult))
                .ExecuteAsync(repairRunId);
        }

        // 4. Re-verification passes this time -> Completed.
        await using (var db = new FactoryDbContext(_options))
        {
            var run = await new AgentRunRepository(db).GetByIdAsync(repairRunId);
            await CreateSync(db, sequenceGate).SyncAsync(run!);
        }

        await using var finalDb = new FactoryDbContext(_options);
        var finalWorkItem = await new WorkItemRepository(finalDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), finalWorkItem!.Status);
        Assert.Equal(1, finalWorkItem.RepairAttemptCount);
        Assert.Equal(2, await finalDb.AgentRuns.CountAsync(x => x.WorkItemId == workItemId));
    }
}
