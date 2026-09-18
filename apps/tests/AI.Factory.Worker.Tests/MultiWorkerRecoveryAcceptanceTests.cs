using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Planning;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Diagnostics;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Shutdown;
using AI.Factory.Worker.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Workstream B — Real Multi-Worker Recovery Acceptance
/// Exercises at least two Worker instances sharing the same WAL-mode SQLite database:
/// 1. Worker A claims an AgentRun.
/// 2. Worker A heartbeats normally.
/// 3. Worker B startup recovery does not terminate Worker A's healthy run.
/// 4. A genuinely stale AgentRun is recovered.
/// 5. Only one worker wins concurrent recovery.
/// 6. Optimistic concurrency collisions are handled without process failure.
/// 7. The recovered run can continue/retry according to existing lifecycle rules.
/// 8. No duplicate successful execution is produced.
/// </summary>
public sealed class MultiWorkerRecoveryAcceptanceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly TimeSpan _staleTimeout = TimeSpan.FromSeconds(3);

    public MultiWorkerRecoveryAcceptanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "multi-worker-acc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "factory.db");
        _connectionString = $"Data Source={_dbPath};Pooling=False";

        var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        using var db = new FactoryDbContext(schemaOptions);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
        AgentDefinitionBootstrapper.EnsureBootstrapDefinitionsAsync(db, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private static string FindWorkerProjectDirectory()
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

        return Path.Combine(dir.FullName, "src", "AI.Factory.Worker");
    }

    private IHost BuildWorkerHost(
        IAgentOrchestrator orchestrator,
        out ApplicationShutdownCoordinator shutdown,
        out BackgroundOperationTracker tracker,
        out OperationalMetrics metrics)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = FindWorkerProjectDirectory(),
            EnvironmentName = "Production"
        });

        Program.ConfigureServices(builder);

        // Swap SQLite for isolated test DB with dedicated connection pool
        var dbDescriptor = builder.Services.Single(d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));
        builder.Services.Remove(dbDescriptor);
        builder.Services.AddDbContext<FactoryDbContext>(options => options.UseSqlite(_connectionString));

        var recoveryDescriptor = builder.Services.Single(d => d.ServiceType == typeof(RecoveryOptions));
        builder.Services.Remove(recoveryDescriptor);
        builder.Services.AddSingleton(new RecoveryOptions
        {
            StaleExecutionTimeout = _staleTimeout,
            PeriodicSweepInterval = TimeSpan.FromHours(1)
        });

        var workerDescriptor = builder.Services.Single(d => d.ServiceType == typeof(AgentWorkerOptions));
        builder.Services.Remove(workerDescriptor);
        builder.Services.AddSingleton(new AgentWorkerOptions
        {
            PollingInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            ExecutionTimeout = TimeSpan.FromMinutes(2)
        });

        // Swap orchestrator
        var orchDescriptor = builder.Services.Single(d => d.ServiceType == typeof(IAgentOrchestrator));
        builder.Services.Remove(orchDescriptor);
        builder.Services.AddSingleton<IAgentOrchestrator>(orchestrator);

        var host = builder.Build();
        shutdown = (ApplicationShutdownCoordinator)host.Services.GetRequiredService<IApplicationShutdown>();
        tracker = (BackgroundOperationTracker)host.Services.GetRequiredService<IBackgroundOperationTracker>();
        metrics = (OperationalMetrics)host.Services.GetRequiredService<IOperationalMetrics>();
        return host;
    }

    [Fact]
    public async Task TwoWorkers_SharingSqlite_ProveNonInterference_ConcurrentRecovery_AndNoDuplicateExecution()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var runTcs = new TaskCompletionSource<bool>();
        var executionCounts = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();

        // Orchestrator that pauses on the initial execution of runA to verify heartbeats and Worker B non-interference
        var orchestrator = new TestAgentOrchestrator(async (runId, token) =>
        {
            executionCounts.AddOrUpdate(runId, 1, (_, count) => count + 1);

            // Wait until test signals continuation
            await runTcs.Task.WaitAsync(token);

            return new AgentOrchestrationResult(
                Executed: true,
                Success: true,
                FinalStatus: AgentRunStatus.Succeeded,
                Output: "completed",
                DurationMs: 10,
                ToolCallNames: Array.Empty<string>(),
                AvailableToolNames: Array.Empty<string>(),
                ErrorMessage: null,
                FailureCategory: null,
                CorrelationId: Guid.NewGuid());
        }, _connectionString);

        // Worker A setup
        using var hostA = BuildWorkerHost(orchestrator, out var shutdownA, out var trackerA, out var metricsA);
        var spA = hostA.Services;

        // Worker B setup
        using var hostB = BuildWorkerHost(orchestrator, out var shutdownB, out var trackerB, out var metricsB);
        var spB = hostB.Services;

        // 1. Seed a Project and WorkItem in shared SQLite
        Guid projectId;
        Guid workItemIdA;
        Guid codingDefId;

        await using (var scope = spA.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var def = await db.AgentDefinitions.FirstAsync(d => d.AgentType == WorkItemType.Implementation.ToString(), ct);
            codingDefId = def.Id;

            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "MultiWorker Demo",
                Slug = "multi-worker-demo",
                RootPath = "/work/multi",
                Status = ProjectStatus.Implementation
            };
            db.Projects.Add(project);
            projectId = project.Id;

            var itemA = new WorkItem
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Title = "Task A - Concurrent Recovery Verification",
                Status = WorkItemStatus.Running.ToString(),
                AttemptCount = 1,
                MaxAttempts = 3,
                StartedAtUtc = DateTime.UtcNow,
                LastHeartbeatAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            db.WorkItems.Add(itemA);
            workItemIdA = itemA.Id;

            await db.SaveChangesAsync(ct);
        }

        // Queue AgentRun for Task A
        Guid runAId;
        await using (var scope = spA.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
            var run = await repo.CreateAsync(new AgentRun
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                WorkItemId = workItemIdA,
                AgentDefinitionId = codingDefId,
                Status = AgentRunStatus.Queued,
                Attempt = 1,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            }, ct);
            runAId = run.Id;
        }

        // 2. Worker A starts polling and claims runA
        var workerA = new AI.Factory.Worker.AgentRunWorker(
            spA.GetRequiredService<IServiceScopeFactory>(),
            spA.GetRequiredService<AgentWorkerOptions>(),
            shutdownA,
            trackerA,
            metricsA,
            NullLogger<AI.Factory.Worker.AgentRunWorker>.Instance);

        _ = Task.Run(() => workerA.StartAsync(ct), ct);

        // Wait until Worker A has claimed runA into Running status
        await using (var scope = spA.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var sw = Stopwatch.StartNew();
            AgentRun? claimed = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                claimed = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runAId, ct);
                if (claimed?.Status == AgentRunStatus.Running)
                {
                    break;
                }
                await Task.Delay(50, ct);
            }

            Assert.NotNull(claimed);
            Assert.Equal(AgentRunStatus.Running, claimed!.Status);
        }

        // 3. Worker A heartbeats normally: let heartbeat loop run for 200ms
        await Task.Delay(200, ct);

        DateTime heartbeatTimeA;
        await using (var scope = spA.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var runA = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == runAId, ct);
            heartbeatTimeA = runA.UpdatedAtUtc;
            Assert.True(heartbeatTimeA > DateTime.UtcNow.AddSeconds(-2));
        }

        // 4. Worker B startup recovery does NOT terminate Worker A's healthy run
        var recoveryB = new WorkflowRecoveryHostedService(
            spB.GetRequiredService<IServiceScopeFactory>(),
            spB.GetRequiredService<RecoveryOptions>(),
            NullLogger<WorkflowRecoveryHostedService>.Instance,
            shutdownB);

        // Execute Worker B startup recovery
        _ = Task.Run(() => recoveryB.StartAsync(ct), ct);
        await Task.Delay(150, ct);

        // Assert Worker A's run is STILL Running (not terminated or orphaned)
        await using (var scope = spB.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var runACheck = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == runAId, ct);
            Assert.Equal(AgentRunStatus.Running, runACheck.Status);
            Assert.Null(runACheck.ErrorMessage);

            var itemACheck = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workItemIdA, ct);
            Assert.Equal(WorkItemStatus.Running.ToString(), itemACheck.Status);
        }

        // 5. Seed a genuinely stale AgentRun in the shared database
        Guid workItemIdStale;
        Guid runStaleId;
        var staleTime = DateTime.UtcNow.AddMinutes(-10);

        await using (var scope = spA.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var itemStale = new WorkItem
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Title = "Task Stale - Orphan Recovery Target",
                Status = WorkItemStatus.Running.ToString(),
                AttemptCount = 1,
                MaxAttempts = 3,
                StartedAtUtc = staleTime,
                LastHeartbeatAtUtc = staleTime,
                UpdatedAtUtc = staleTime
            };
            db.WorkItems.Add(itemStale);
            workItemIdStale = itemStale.Id;

            var runStale = new AgentRun
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                WorkItemId = workItemIdStale,
                AgentDefinitionId = codingDefId,
                Status = AgentRunStatus.Running,
                Attempt = 1,
                StartedAtUtc = staleTime,
                CreatedAtUtc = staleTime,
                UpdatedAtUtc = staleTime
            };
            db.AgentRuns.Add(runStale);
            runStaleId = runStale.Id;

            await db.SaveChangesAsync(ct);
        }

        // 6. Concurrent recovery sweep: Both Worker A and Worker B run recovery simultaneously
        // Proves:
        // - Only one worker wins recovery
        // - Concurrency collision handled gracefully without process crash
        var recoveryTaskA = Task.Run(async () =>
        {
            await using var scope = spA.CreateAsyncScope();
            var taskManager = scope.ServiceProvider.GetRequiredService<ITaskStateManager>();
            var runRepo = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
            var tasksRecovered = await taskManager.RecoverStaleExecutionsAsync(ct);
            var runsRecovered = await runRepo.RecoverOrphanedRunsAsync(DateTime.UtcNow - _staleTimeout, ct);
            return (tasksRecovered, runsRecovered);
        }, ct);

        var recoveryTaskB = Task.Run(async () =>
        {
            await using var scope = spB.CreateAsyncScope();
            var taskManager = scope.ServiceProvider.GetRequiredService<ITaskStateManager>();
            var runRepo = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
            var tasksRecovered = await taskManager.RecoverStaleExecutionsAsync(ct);
            var runsRecovered = await runRepo.RecoverOrphanedRunsAsync(DateTime.UtcNow - _staleTimeout, ct);
            return (tasksRecovered, runsRecovered);
        }, ct);

        var results = await Task.WhenAll(recoveryTaskA, recoveryTaskB);
        var resA = results[0];
        var resB = results[1];

        // Exactly one worker won the task recovery and run recovery across concurrent attempts
        var totalTasksRecovered = resA.tasksRecovered + resB.tasksRecovered;
        var totalRunsRecovered = resA.runsRecovered + resB.runsRecovered;

        Assert.Equal(1, totalTasksRecovered);
        Assert.Equal(1, totalRunsRecovered);

        // 7. Verify the stale run transitioned to Failed with OrphanedMarker, and WorkItem to Repairing
        await using (var scope = spA.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var recoveredRun = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == runStaleId, ct);
            Assert.Equal(AgentRunStatus.Failed, recoveredRun.Status);
            Assert.True(AgentRunRecovery.WasOrphaned(recoveredRun));
            Assert.Equal(FailureCategory.Infrastructure, recoveredRun.FailureCategory);

            var recoveredItem = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workItemIdStale, ct);
            Assert.Equal(WorkItemStatus.Repairing.ToString(), recoveredItem.Status);
        }

        // 8. Unblock Worker A's initial run and let it complete normally
        runTcs.SetResult(true);

        // Wait for runA to reach Succeeded
        await using (var scope = spA.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var sw = Stopwatch.StartNew();
            AgentRun? completedRunA = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(45))
            {
                completedRunA = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runAId, ct);
                if (completedRunA?.Status == AgentRunStatus.Succeeded)
                {
                    break;
                }
                await Task.Delay(50, ct);
            }

            Assert.NotNull(completedRunA);
            Assert.Equal(AgentRunStatus.Succeeded, completedRunA!.Status);
        }

        // Clean shutdown of worker and recovery services
        await workerA.StopAsync(CancellationToken.None);
        await recoveryB.StopAsync(CancellationToken.None);

        // 9. Assert no duplicate successful execution was produced for Task A
        Assert.Equal(1, executionCounts[runAId]);
    }

    private sealed class TestProjectPlanner : IProjectPlanner
    {
        public Task<AgentRun> GenerateAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<PlannerMaterializationResult> MaterializeAsync(Guid agentRunId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlannerMaterializationResult(PlannerMaterializationOutcome.Skipped, null, "test"));

        public Task<AgentRun?> GetLatestRunAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentRun?>(null);

        public Task<IReadOnlyList<AgentRun>> GetRunsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentRun>>(Array.Empty<AgentRun>());
    }

    private sealed class TestAgentOrchestrator : IAgentOrchestrator
    {
        private readonly Func<Guid, CancellationToken, Task<AgentOrchestrationResult>> _behavior;
        private readonly string _connectionString;

        public TestAgentOrchestrator(
            Func<Guid, CancellationToken, Task<AgentOrchestrationResult>> behavior,
            string connectionString)
        {
            _behavior = behavior;
            _connectionString = connectionString;
        }

        public async Task<AgentOrchestrationResult> ExecuteAsync(Guid agentRunId, CancellationToken cancellationToken = default)
        {
            var result = await _behavior(agentRunId, cancellationToken);

            if (result.Executed)
            {
                var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connectionString).Options;
                var updated = false;
                for (var attempt = 0; attempt < 10 && !updated; attempt++)
                {
                    try
                    {
                        await using var db = new FactoryDbContext(options);
                        var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == agentRunId, cancellationToken);
                        if (run is not null && run.Status == AgentRunStatus.Running)
                        {
                            run.TransitionTo(result.FinalStatus);
                            run.CompletedAtUtc = DateTime.UtcNow;
                            await db.SaveChangesAsync(cancellationToken);
                            updated = true;
                        }
                        else
                        {
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is ConcurrencyConflictException or DbUpdateConcurrencyException)
                    {
                        await Task.Delay(50, cancellationToken);
                    }
                }
            }

            return result;
        }
    }
}
