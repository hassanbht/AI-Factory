using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
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

/// <summary>Phase 18.3 §10/§17 — generic AgentRun -> WorkItem completion mapping.</summary>
public sealed class AgentRunWorkItemSyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public AgentRunWorkItemSyncTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static AgentRunWorkItemSync CreateSync(FactoryDbContext db, IVerificationGate? verificationGate = null) => new(
        new WorkItemRepository(db), new ProjectRepository(db), new WorkItemExecutionResultMapper(),
        verificationGate ?? new FakeVerificationGate(VerificationStatus.Passed),
        NullLogger<AgentRunWorkItemSync>.Instance);

    /// <summary>Phase 18.8 — deterministic stand-in for the real build/test pipeline.</summary>
    private sealed class FakeVerificationGate : IVerificationGate
    {
        private readonly VerificationStatus _status;

        public FakeVerificationGate(VerificationStatus status)
        {
            _status = status;
        }

        public Task<VerificationResult> VerifyAsync(
            Project project, WorkItem workItem, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(_status, $"fake:{_status}", 0, TimeSpan.Zero));

        public Task<VerificationResult> VerifyProjectAsync(
            Project project, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(_status, $"fake:{_status}", 0, TimeSpan.Zero));
    }

    private async Task<Guid> SeedRunningTaskAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await new ProjectService(new ProjectRepository(db)).CreateAsync("Sync Project", "d");
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, "T", null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Running);
        return item.Id;
    }

    [Theory]
    [InlineData(AgentRunStatus.Failed, WorkItemStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled, WorkItemStatus.Cancelled)]
    [InlineData(AgentRunStatus.TimedOut, WorkItemStatus.Failed)]
    public async Task SyncAsync_TerminalRuntimeFailure_TransitionsWorkItem(AgentRunStatus runStatus, WorkItemStatus expected)
    {
        // Phase 18.7 §12 "Runtime failure" — unchanged since Phase 18.3: the
        // AgentRun's own terminal status decides these, no StructuredResult
        // interpretation involved.
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun { WorkItemId = taskId, Status = runStatus };

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db).SyncAsync(run);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(expected.ToString(), workItem!.Status);
    }

    // ---------------- Phase 18.7 — Succeeded AgentRun is interpreted, not trusted blindly ----------------

    /// <summary>
    /// Builds the exact persisted-column shape AgentOrchestrator itself
    /// writes: StructuredAgentResult.Data is a plain string holding raw JSON
    /// text (never a nested JSON object) — serialized here through the same
    /// StructuredAgentResult.JsonOptions the real pipeline uses, so this
    /// never drifts from the actual persistence format.
    /// </summary>
    private static string SucceededStructuredResult(string executionStatus)
    {
        var data = "{\"execution\":{\"version\":1,\"status\":\"" + executionStatus + "\",\"summary\":\"exec summary\"}}";

        var envelope = new StructuredAgentResult(
            StructuredAgentResult.CurrentVersion, StructuredResultStatus.Succeeded, "s", "ok", data,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null);

        return JsonSerializer.Serialize(envelope, StructuredAgentResult.JsonOptions);
    }

    [Fact]
    public async Task SyncAsync_SucceededWithValidCompletedExecutionResult_TransitionsToCompleted()
    {
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun
        {
            WorkItemId = taskId,
            Status = AgentRunStatus.Succeeded,
            StructuredResult = SucceededStructuredResult("completed")
        };

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db).SyncAsync(run);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task SyncAsync_SucceededWithRepairRequiredExecutionResult_TransitionsToBlocked()
    {
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun
        {
            WorkItemId = taskId,
            Status = AgentRunStatus.Succeeded,
            StructuredResult = SucceededStructuredResult("repair_required")
        };

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db).SyncAsync(run);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Blocked.ToString(), workItem!.Status);
        Assert.Equal("exec summary", workItem.CurrentError);
    }

    [Fact]
    public async Task SyncAsync_SucceededWithNoStructuredResult_TransitionsToFailed()
    {
        // Phase 18.7 §12 "Execution result invalid" — a bare Succeeded with
        // no interpretable structured result can no longer be trusted as a
        // completed WorkItem (the gap this phase closes).
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun { WorkItemId = taskId, Status = AgentRunStatus.Succeeded, StructuredResult = null };

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db).SyncAsync(run);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task SyncAsync_SucceededWithMalformedExecutionData_TransitionsToFailed()
    {
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun
        {
            WorkItemId = taskId,
            Status = AgentRunStatus.Succeeded,
            StructuredResult = SucceededStructuredResult("not-a-real-status")
        };

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db).SyncAsync(run);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem!.Status);
    }

    // ---------------- Phase 18.8 — deterministic verification gate ----------------

    [Theory]
    [InlineData(VerificationStatus.Passed, WorkItemStatus.Completed)]
    [InlineData(VerificationStatus.Failed, WorkItemStatus.Repairing)]
    [InlineData(VerificationStatus.InfrastructureError, WorkItemStatus.Failed)]
    [InlineData(VerificationStatus.TimedOut, WorkItemStatus.Failed)]
    [InlineData(VerificationStatus.Cancelled, WorkItemStatus.Cancelled)]
    public async Task SyncAsync_SucceededExecution_RunsVerificationGate_AndAppliesFailurePolicy(
        VerificationStatus gateStatus, WorkItemStatus expected)
    {
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun
        {
            WorkItemId = taskId,
            Status = AgentRunStatus.Succeeded,
            StructuredResult = SucceededStructuredResult("completed")
        };

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db, new FakeVerificationGate(gateStatus)).SyncAsync(run);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(expected.ToString(), workItem!.Status);

        if (expected != WorkItemStatus.Completed)
        {
            Assert.False(string.IsNullOrWhiteSpace(workItem.CurrentError));
        }
    }

    [Fact]
    public async Task SyncAsync_SucceededExecution_DependentWorkItem_NotSatisfiedWhileTesting()
    {
        // Phase 18.8 §9 — a WorkItem sitting in Testing (i.e. the gate has
        // not yet resolved it) must not be treated as satisfying a
        // dependency. A gate that never returns proves this deterministically
        // by never letting the WorkItem leave Testing during the assertion.
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun
        {
            WorkItemId = taskId,
            Status = AgentRunStatus.Succeeded,
            StructuredResult = SucceededStructuredResult("completed")
        };

        await using var db = new FactoryDbContext(_options);

        var gate = new BlockingVerificationGate();
        var syncTask = CreateSync(db, gate).SyncAsync(run);

        await gate.EnteredAsync();

        await using (var midDb = new FactoryDbContext(_options))
        {
            var workItem = await new WorkItemRepository(midDb).GetByIdAsync(taskId);
            Assert.Equal(WorkItemStatus.Testing.ToString(), workItem!.Status);

            // TaskDependencyResolverTests.Resolve_TestingTarget_DependentStaysWaiting proves
            // separately (and unconditionally, not just for the window this gate happens to
            // block) that ITaskDependencyResolver never treats a Testing target as satisfied.
        }

        gate.Release();
        await syncTask;
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

    [Fact]
    public async Task SyncAsync_AlreadyTerminalWorkItem_NotOverwritten()
    {
        var taskId = await SeedRunningTaskAsync();

        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
            await workItems.TransitionStatusAsync(taskId, WorkItemStatus.Completed);
        }

        var run = new AgentRun { WorkItemId = taskId, Status = AgentRunStatus.Failed };

        await using var syncDb = new FactoryDbContext(_options);
        await CreateSync(syncDb).SyncAsync(run);

        var workItem = await new WorkItemRepository(syncDb).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task SyncAsync_NoWorkItemId_NoOp()
    {
        var run = new AgentRun { WorkItemId = null, Status = AgentRunStatus.Succeeded };

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db).SyncAsync(run);

        // No exception, no WorkItem touched — nothing further to assert.
    }
}
