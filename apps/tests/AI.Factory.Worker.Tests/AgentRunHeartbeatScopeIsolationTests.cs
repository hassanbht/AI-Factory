using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Diagnostics;
using AI.Factory.Infrastructure.Shutdown;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase 18.29.1 — the heartbeat loop must not share a DI scope, and therefore must
/// not share a FactoryDbContext, with the agent execution it runs alongside.
///
/// The two deliberately overlap in time: the heartbeat exists precisely to prove a
/// long execution is still alive. Resolving both from one scope therefore guarantees
/// concurrent operations on one scoped DbContext, which EF Core rejects with
/// "A second operation was started on this context instance before a previous
/// operation completed". It stayed invisible until a real containerized
/// Implementation run wrote workspace artifacts while a heartbeat tick was in
/// flight, and it failed the AgentRun outright.
///
/// These tests assert the scope boundary directly rather than trying to reproduce a
/// timing race, so they stay deterministic.
/// </summary>
public sealed class AgentRunHeartbeatScopeIsolationTests
{
    /// <summary>Scoped marker: one instance per DI scope, so instance identity IS scope identity.</summary>
    private sealed class ScopeTag
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class ScopeRecordingTaskStateManager : ITaskStateManager
    {
        private readonly ScopeTag _tag;
        private readonly ConcurrentScopeLog _log;

        public ScopeRecordingTaskStateManager(ScopeTag tag, ConcurrentScopeLog log)
        {
            _tag = tag;
            _log = log;
        }

        public Task HeartbeatAsync(Guid workItemId, string? note = null, CancellationToken cancellationToken = default)
        {
            _log.RecordHeartbeatScope(_tag.Id);
            return Task.CompletedTask;
        }

        public Task<bool> TryStartAsync(Guid workItemId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task CompleteAsync(Guid workItemId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> FailAsync(Guid workItemId, string error, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task RecoverOrphanedTasksAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> RecoverStaleExecutionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class ConcurrentScopeLog
    {
        private readonly List<Guid> _heartbeatScopes = new();

        public Guid? ExecutionScopeId { get; set; }

        public void RecordHeartbeatScope(Guid scopeId)
        {
            lock (_heartbeatScopes)
            {
                _heartbeatScopes.Add(scopeId);
            }
        }

        public IReadOnlyList<Guid> HeartbeatScopes
        {
            get { lock (_heartbeatScopes) { return _heartbeatScopes.ToList(); } }
        }
    }

    /// <summary>Records the scope it was resolved into — i.e. the execution scope.</summary>
    private sealed class ScopeCapturingOrchestrator : IAgentOrchestrator
    {
        private readonly ScopeTag _tag;
        private readonly ConcurrentScopeLog _log;

        public ScopeCapturingOrchestrator(ScopeTag tag, ConcurrentScopeLog log)
        {
            _tag = tag;
            _log = log;
        }

        public Task<AgentOrchestrationResult> ExecuteAsync(
            Guid agentRunId, CancellationToken cancellationToken = default)
        {
            _log.ExecutionScopeId = _tag.Id;
            return Task.FromResult(new AgentOrchestrationResult(
                Executed: true,
                Success: true,
                FinalStatus: AgentRunStatus.Succeeded,
                Output: null,
                DurationMs: 1,
                ToolCallNames: Array.Empty<string>(),
                AvailableToolNames: Array.Empty<string>(),
                ErrorMessage: null,
                FailureCategory: null,
                CorrelationId: Guid.NewGuid()));
        }
    }

    private sealed class SingleRunRepository : IAgentRunRepository
    {
        private readonly AgentRun _run;
        private bool _claimed;

        public SingleRunRepository(AgentRun run) => _run = run;

        public Task<AgentRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default)
        {
            if (_claimed)
            {
                return Task.FromResult<AgentRun?>(null);
            }

            _claimed = true;
            return Task.FromResult<AgentRun?>(_run);
        }

        public Task HeartbeatAsync(Guid agentRunId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AgentRun?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentRun?>(_run);

        public Task<AgentRun> CreateAsync(AgentRun run, CancellationToken cancellationToken = default) =>
            Task.FromResult(run);

        public Task UpdateAsync(AgentRun run, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CountByStatusAsync(AgentRunStatus status, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<AgentRun?> GetByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentRun?>(_run);

        public Task<AgentRunPage> GetPagedAsync(AgentRunQuery query, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> HasActiveRunsForProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> RecoverOrphanedRunsAsync(DateTime? olderThanUtc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    [Fact]
    public async Task Heartbeat_RunsInItsOwnScope_NotTheExecutionScope()
    {
        var log = new ConcurrentScopeLog();

        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            Status = AgentRunStatus.Queued
        };

        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddScoped<ScopeTag>();
        services.AddSingleton<IAgentRunRepository>(new SingleRunRepository(run));
        services.AddScoped<ITaskStateManager>(sp =>
            new ScopeRecordingTaskStateManager(sp.GetRequiredService<ScopeTag>(), log));
        services.AddScoped<IAgentOrchestrator>(sp =>
            new ScopeCapturingOrchestrator(sp.GetRequiredService<ScopeTag>(), log));
        services.AddSingleton<IAgentRunWorkItemSync>(new NoOpWorkItemSync());

        var provider = services.BuildServiceProvider();
        using var shutdown = new ApplicationShutdownCoordinator();

        var worker = new AI.Factory.Worker.AgentRunWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AgentWorkerOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromMilliseconds(20)
            },
            shutdown,
            new BackgroundOperationTracker(),
            new OperationalMetrics(),
            NullLogger<AI.Factory.Worker.AgentRunWorker>.Instance);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline
            && (log.ExecutionScopeId is null || log.HeartbeatScopes.Count == 0))
        {
            await Task.Delay(25, CancellationToken.None);
        }

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(log.ExecutionScopeId);
        Assert.NotEmpty(log.HeartbeatScopes);

        // The invariant: no heartbeat ever ran in the execution scope. Sharing it is
        // what put two concurrent operations on one DbContext.
        Assert.DoesNotContain(log.ExecutionScopeId!.Value, log.HeartbeatScopes);

        // And every heartbeat shared one scope of its own, rather than leaking a new
        // scope (and a new DbContext) per tick.
        Assert.Single(log.HeartbeatScopes.Distinct());
    }

    private sealed class NoOpWorkItemSync : IAgentRunWorkItemSync
    {
        public Task SyncAsync(AgentRun agentRun, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
