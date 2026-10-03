using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Shutdown;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Worker.Tests;

public sealed class OrchestrationRunWorkerShutdownTests
{
    [Fact]
    public async Task Worker_DoesNotClaimWork_WhenShutdownIsStopping()
    {
        var runRepository = new TrackingRunRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IOrchestrationRunRepository>(runRepository);
        var serviceProvider = services.BuildServiceProvider();

        using var shutdown = new ApplicationShutdownCoordinator();
        shutdown.TryBeginShutdown(); // Shutdown already in progress

        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();
        var options = new OrchestrationOptions { QueuePollingInterval = TimeSpan.FromMilliseconds(50) };
        var shutdownOptions = new ShutdownOptions { GracefulTimeoutSeconds = 1, ForceCancellationAfterSeconds = 1 };

        var worker = new OrchestrationRunWorker(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            shutdownOptions,
            shutdown,
            tracker,
            metrics,
            NullLogger<OrchestrationRunWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await worker.StartAsync(cts.Token);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, runRepository.TryClaimCalls);
    }

    [Fact]
    public async Task StopAsync_WaitsForDrain_AndTriggersShutdown()
    {
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();

        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();
        var options = new OrchestrationOptions();
        var shutdownOptions = new ShutdownOptions { GracefulTimeoutSeconds = 2, ForceCancellationAfterSeconds = 1 };

        var worker = new OrchestrationRunWorker(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            shutdownOptions,
            shutdown,
            tracker,
            metrics,
            NullLogger<OrchestrationRunWorker>.Instance);

        var context = new OperationContext(
            Guid.NewGuid(), Guid.NewGuid(), null, "ActiveTask", null, null, null, DateTime.UtcNow);

        var handle = tracker.Track(context);
        Assert.Equal(1, tracker.ActiveCount);

        var stopTask = Task.Run(async () =>
        {
            await worker.StopAsync(CancellationToken.None);
        });

        // Verify shutdown initiated while draining
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!shutdown.IsStopping && sw.ElapsedMilliseconds < 1000)
        {
            await Task.Delay(10);
        }

        Assert.True(shutdown.IsStopping);
        Assert.False(stopTask.IsCompleted);

        // Complete the operation
        handle.Dispose();

        await stopTask;
        Assert.Equal(0, tracker.ActiveCount);
    }

    private sealed class TrackingRunRepository : IOrchestrationRunRepository
    {
        public int TryClaimCalls { get; private set; }

        public Task<OrchestrationRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default)
        {
            TryClaimCalls++;
            return Task.FromResult<OrchestrationRun?>(null);
        }

        public Task<OrchestrationRun> CreateAsync(OrchestrationRun run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRun?> GetActiveByWorkItemIdAsync(Guid workItemId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateAsync(OrchestrationRun run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddAttemptAsync(OrchestrationRunAttempt attempt, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddProgressEventAsync(OrchestrationProgressEvent progressEvent, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRunStep> AddStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRunStep?> FindLatestOpenStepAsync(Guid orchestrationRunId, Domain.Enums.OrchestrationStepKind stage, int? attemptNumber, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RenewLeaseAsync(Guid runId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> RecoverStaleLeasesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ReleaseLeaseAsync(Guid runId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
