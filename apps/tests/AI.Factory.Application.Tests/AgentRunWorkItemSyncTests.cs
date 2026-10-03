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

    private static AgentRunWorkItemSync CreateSync(
        FactoryDbContext db,
        IVerificationGate? verificationGate = null,
        IWorkItemCompletionProcessor? completionProcessor = null) => new(
        new WorkItemRepository(db), new ProjectRepository(db), new WorkItemExecutionResultMapper(),
        verificationGate ?? new FakeVerificationGate(VerificationStatus.Passed),
        completionProcessor ?? new FakeWorkItemCompletionProcessor(WorkItemStatus.Completed, null),
        NullLogger<AgentRunWorkItemSync>.Instance);

    /// <summary>
    /// Phase 18.13 — stand-in for the real review/evaluation/learning stage;
    /// these tests care only about AgentRunWorkItemSync's own delegation and
    /// transition-application behavior, not WorkItemCompletionProcessor's
    /// internals (covered separately by WorkItemCompletionProcessorTests).
    /// </summary>
    private sealed class FakeWorkItemCompletionProcessor : IWorkItemCompletionProcessor
    {
        private readonly WorkItemStatus _target;
        private readonly string? _errorMessage;

        public FakeWorkItemCompletionProcessor(WorkItemStatus target, string? errorMessage)
        {
            _target = target;
            _errorMessage = errorMessage;
        }

        public Task<WorkItemCompletionDecision> ProcessAsync(
            Project project, WorkItem workItem, AgentRun agentRun, VerificationResult verificationResult,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemCompletionDecision(_target, _errorMessage));
    }

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
        var repo = new ProjectRepository(db);
        var project = await new ProjectService(repo).CreateAsync("Sync Project", "d");
        // Phase 18.16 §12 — WorkItem creation now requires a source decision
        // (Status != Created); this file is not exercising source init.
        project.Status = ProjectStatus.Analyzing;
        await repo.SaveChangesAsync();
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, "T", null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Running);
        return item.Id;
    }

    [Theory]
    [InlineData(AgentRunStatus.Failed, WorkItemStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled, WorkItemStatus.Cancelled)]
    // Phase M — a timeout is retryable: with budget left it returns the WorkItem to Ready.
    [InlineData(AgentRunStatus.TimedOut, WorkItemStatus.Ready)]
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

    // ---------------- Phase M — bounded runtime retry (benchmark factory-T1-r2) ----------------

    private async Task SetAttemptBudgetAsync(Guid workItemId, int attemptCount, int maxAttempts)
    {
        await using var db = new FactoryDbContext(_options);
        var item = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        item.AttemptCount = attemptCount;
        item.MaxAttempts = maxAttempts;
        await db.SaveChangesAsync();
    }

    private async Task<WorkItem> SyncAndReloadAsync(AgentRun run, IAuditLogger? audit = null)
    {
        await using (var db = new FactoryDbContext(_options))
        {
            var sync = new AgentRunWorkItemSync(
                new WorkItemRepository(db), new ProjectRepository(db), new WorkItemExecutionResultMapper(),
                new FakeVerificationGate(VerificationStatus.Passed),
                new FakeWorkItemCompletionProcessor(WorkItemStatus.Completed, null),
                NullLogger<AgentRunWorkItemSync>.Instance, audit);
            await sync.SyncAsync(run);
        }

        await using var verify = new FactoryDbContext(_options);
        return (await new WorkItemRepository(verify).GetByIdAsync(run.WorkItemId!.Value))!;
    }

    [Fact]
    public async Task TimedOutAttempt_WithBudgetLeft_IsRetried_CountedAndAudited()
    {
        var taskId = await SeedRunningTaskAsync();
        var audit = new CapturingAuditLogger();
        var run = new AgentRun
        {
            WorkItemId = taskId, Status = AgentRunStatus.TimedOut, FailureCategory = FailureCategory.Timeout,
            ErrorMessage = "Execution timed out after 285 seconds."
        };

        var workItem = await SyncAndReloadAsync(run, audit);

        Assert.Equal(WorkItemStatus.Ready.ToString(), workItem.Status);
        Assert.Equal(1, workItem.AttemptCount); // never again "TimedOut, attemptCount 0"
        Assert.Contains("Attempt 1 of 3 ended 'TimedOut'", workItem.CurrentError);
        Assert.Contains("Retry scheduled", workItem.CurrentError);
        Assert.Contains(audit.Events, e => e == AuditEventType.Retry);
    }

    [Fact]
    public async Task TimedOutAttempt_WhenBudgetIsExhausted_FailsWithAReason()
    {
        var taskId = await SeedRunningTaskAsync();
        await SetAttemptBudgetAsync(taskId, attemptCount: 2, maxAttempts: 3);

        var workItem = await SyncAndReloadAsync(new AgentRun { WorkItemId = taskId, Status = AgentRunStatus.TimedOut });

        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem.Status);
        Assert.Equal(3, workItem.AttemptCount);
        Assert.Contains("budget exhausted", workItem.CurrentError);
    }

    [Fact]
    public async Task TimedOutAttempt_WhenPolicyAllowsNoRetry_Fails()
    {
        var taskId = await SeedRunningTaskAsync();
        await SetAttemptBudgetAsync(taskId, attemptCount: 0, maxAttempts: 1);

        var workItem = await SyncAndReloadAsync(new AgentRun { WorkItemId = taskId, Status = AgentRunStatus.TimedOut });

        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem.Status);
        Assert.Equal(1, workItem.AttemptCount);
    }

    [Fact]
    public async Task OrphanedRun_InfrastructureFailure_IsRetried()
    {
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun
        {
            WorkItemId = taskId, Status = AgentRunStatus.Failed, FailureCategory = FailureCategory.Infrastructure,
            ErrorMessage = $"{AgentRunRecovery.OrphanedMarker}: no heartbeat."
        };

        var workItem = await SyncAndReloadAsync(run);

        Assert.Equal(WorkItemStatus.Ready.ToString(), workItem.Status);
        Assert.Equal(1, workItem.AttemptCount);
    }

    [Theory]
    [InlineData(FailureCategory.Agent)]
    [InlineData(FailureCategory.Validation)]
    [InlineData(FailureCategory.Provider)]
    public async Task NonRetryableFailure_FailsImmediately_ButStillConsumesAnAttempt(FailureCategory category)
    {
        var taskId = await SeedRunningTaskAsync();

        var workItem = await SyncAndReloadAsync(new AgentRun
        {
            WorkItemId = taskId, Status = AgentRunStatus.Failed, FailureCategory = category, ErrorMessage = "boom"
        });

        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem.Status);
        Assert.Equal(1, workItem.AttemptCount);
        Assert.Contains("Not retryable", workItem.CurrentError);
    }

    [Fact]
    public async Task CancelledRun_IsNeverRetried()
    {
        var taskId = await SeedRunningTaskAsync();

        var workItem = await SyncAndReloadAsync(new AgentRun { WorkItemId = taskId, Status = AgentRunStatus.Cancelled });

        Assert.Equal(WorkItemStatus.Cancelled.ToString(), workItem.Status);
        Assert.Equal(0, workItem.AttemptCount);
    }

    [Fact]
    public async Task RepeatedSyncOfTheSameTimedOutRun_RetriesExactlyOnce()
    {
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun { WorkItemId = taskId, Status = AgentRunStatus.TimedOut };

        await SyncAndReloadAsync(run);
        var workItem = await SyncAndReloadAsync(run);

        Assert.Equal(WorkItemStatus.Ready.ToString(), workItem.Status);
        Assert.Equal(1, workItem.AttemptCount);
    }

    private sealed class CapturingAuditLogger : IAuditLogger
    {
        public List<AuditEventType> Events { get; } = new();

        public Task LogAsync(
            Guid executionId, AuditEventType eventType, AuditSeverity severity, string message,
            Guid? projectId = null, Guid? workItemId = null, IReadOnlyDictionary<string, string>? metadata = null,
            Guid? correlationId = null, Guid? operationId = null, Guid? runId = null, string? outcome = null,
            string? actorType = null, string? actorId = null, long? durationMs = null,
            CancellationToken cancellationToken = default)
        {
            Events.Add(eventType);
            return Task.CompletedTask;
        }
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
    public async Task SyncAsync_SucceededWithNoStructuredResult_GoesToVerificationNotFailed()
    {
        // Phase 18.20 §4 — an unusable *report* is not evidence the *work*
        // failed. A Succeeded AgentRun with no interpretable structured result
        // is handed to the Factory's own verification instead of dead-ending,
        // which is exactly what the live coding model observably needs
        // (Phase 18.19 §11). A gate that never returns pins the WorkItem in
        // Testing so this asserts the routing decision itself, not whatever
        // verification would have concluded afterwards.
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun { WorkItemId = taskId, Status = AgentRunStatus.Succeeded, StructuredResult = null };

        await using var db = new FactoryDbContext(_options);

        var gate = new BlockingVerificationGate();
        var syncTask = CreateSync(db, gate).SyncAsync(run);
        await gate.EnteredAsync();

        await using (var midDb = new FactoryDbContext(_options))
        {
            var inFlight = await new WorkItemRepository(midDb).GetByIdAsync(taskId);
            Assert.Equal(WorkItemStatus.Testing.ToString(), inFlight!.Status);
            // Nothing has actually gone wrong yet — no error may be recorded.
            Assert.Null(inFlight.CurrentError);
        }

        gate.Release();
        await syncTask;
    }

    [Fact]
    public async Task SyncAsync_SucceededWithMalformedExecutionData_GoesToVerificationNotFailed()
    {
        // Phase 18.20 §6 — malformed metadata is never reinterpreted as valid
        // (the mapper still rejects it); it is only treated as "no usable
        // report", so the Factory decides via verification.
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun
        {
            WorkItemId = taskId,
            Status = AgentRunStatus.Succeeded,
            StructuredResult = SucceededStructuredResult("not-a-real-status")
        };

        await using var db = new FactoryDbContext(_options);

        var gate = new BlockingVerificationGate();
        var syncTask = CreateSync(db, gate).SyncAsync(run);
        await gate.EnteredAsync();

        await using (var midDb = new FactoryDbContext(_options))
        {
            var inFlight = await new WorkItemRepository(midDb).GetByIdAsync(taskId);
            Assert.Equal(WorkItemStatus.Testing.ToString(), inFlight!.Status);
        }

        gate.Release();
        await syncTask;
    }

    [Theory]
    [InlineData(VerificationStatus.Passed, WorkItemStatus.Completed)]
    [InlineData(VerificationStatus.Failed, WorkItemStatus.Repairing)]
    public async Task SyncAsync_SucceededWithoutMetadata_FactoryVerificationDecidesOutcome(
        VerificationStatus gateStatus, WorkItemStatus expected)
    {
        // Phase 18.20 §9 — the whole point of routing an unusable report to
        // Testing: verification, not the agent, produces the final answer.
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun { WorkItemId = taskId, Status = AgentRunStatus.Succeeded, StructuredResult = null };

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db, new FakeVerificationGate(gateStatus)).SyncAsync(run);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(expected.ToString(), workItem!.Status);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("not-a-real-status")]
    [InlineData(null)]
    public async Task SyncAsync_SucceededAgentRun_NeverReachesCompletedWithoutVerification(string? executionStatus)
    {
        // Phase 18.20 §8 invariant: AgentRun.Succeeded NEVER maps straight to
        // WorkItem.Completed, for any report shape the agent can produce —
        // including the one where it explicitly claims completion. A gate that
        // fails everything proves no report can bypass it.
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun
        {
            WorkItemId = taskId,
            Status = AgentRunStatus.Succeeded,
            StructuredResult = executionStatus is null ? null : SucceededStructuredResult(executionStatus)
        };

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db, new FakeVerificationGate(VerificationStatus.Failed)).SyncAsync(run);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.NotEqual(WorkItemStatus.Completed.ToString(), workItem!.Status);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), workItem.Status);
    }

    [Theory]
    [InlineData(StructuredResultStatus.Failed)]
    [InlineData(StructuredResultStatus.TimedOut)]
    [InlineData(StructuredResultStatus.Cancelled)]
    public async Task SyncAsync_ExitZeroRun_WhoseOwnReportSaysFailed_FailsTheWorkItem_WithoutVerification(StructuredResultStatus reported)
    {
        // Phase P (observed live, OpenHands + qwen/qwen3-coder): the runtime exited 0, the agent's valid
        // structured result said {"status":"Failed", "summary":"... could not verify tests pass ..."}, and the
        // sync treated that like missing metadata — Testing -> verification -> review -> Completed. An explicit
        // failure report is not unusable metadata; the WorkItem must stay failed, even when a verification gate
        // would have passed.
        var taskId = await SeedRunningTaskAsync();
        var envelope = new StructuredAgentResult(
            StructuredAgentResult.CurrentVersion, reported, "could not verify tests pass", "out", null,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null);
        var run = new AgentRun
        {
            WorkItemId = taskId,
            Status = AgentRunStatus.Succeeded,
            StructuredResult = JsonSerializer.Serialize(envelope, StructuredAgentResult.JsonOptions)
        };

        await using var db = new FactoryDbContext(_options);
        var gate = new CountingVerificationGate();
        await CreateSync(db, gate).SyncAsync(run);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem!.Status);
        Assert.Contains($"'{reported}'", workItem.CurrentError);
        Assert.Contains("could not verify tests pass", workItem.CurrentError);
        Assert.Equal(0, gate.Calls);
    }

    private sealed class CountingVerificationGate : IVerificationGate
    {
        public int Calls { get; private set; }

        public Task<VerificationResult> VerifyAsync(Project project, WorkItem workItem, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new VerificationResult(VerificationStatus.Passed, "counted", 0, TimeSpan.Zero));
        }

        public Task<VerificationResult> VerifyProjectAsync(Project project, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("VerifyProjectAsync should not be called in this test file.");
    }

    [Fact]
    public async Task SyncAsync_SucceededWithoutMetadata_RepeatedSync_IsIdempotent()
    {
        // Phase 18.20 §28.11 — the new routing must not weaken the existing
        // idempotency guard: a second observation of the same terminal
        // AgentRun finds the WorkItem no longer Running and changes nothing.
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun { WorkItemId = taskId, Status = AgentRunStatus.Succeeded, StructuredResult = null };

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db, new FakeVerificationGate(VerificationStatus.Passed)).SyncAsync(run);

        var afterFirst = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), afterFirst!.Status);

        await CreateSync(db, new FakeVerificationGate(VerificationStatus.Failed)).SyncAsync(run);

        var afterSecond = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), afterSecond!.Status);
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
    public async Task SyncAsync_AlreadyCancelledWorkItem_NotResurrectedByLateFinishingAgentRun()
    {
        // Phase 18.13 — a WorkItem cancelled via OrchestrationService's
        // cooperative cancellation must never be resurrected by an AgentRun
        // that was already in flight and only finishes afterward. This is
        // the existing idempotency guard (WorkItem must still be Running),
        // exercised specifically against a Cancelled-not-Completed terminal
        // status.
        var taskId = await SeedRunningTaskAsync();

        await using (var db = new FactoryDbContext(_options))
        {
            var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
            workItem!.TransitionTo(WorkItemStatus.Cancelled);
            await new WorkItemRepository(db).UpdateAsync(workItem);
        }

        var run = new AgentRun
        {
            WorkItemId = taskId,
            Status = AgentRunStatus.Succeeded,
            StructuredResult = SucceededStructuredResult("completed")
        };

        await using var syncDb = new FactoryDbContext(_options);
        await CreateSync(syncDb).SyncAsync(run);

        var workItem2 = await new WorkItemRepository(syncDb).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Cancelled.ToString(), workItem2!.Status);
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
