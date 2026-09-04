using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Worker.Tests;

public sealed class WorkerHealthCoordinatorTests
{
    [Fact]
    public void InitialState_IsStarting_AndNotReady()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);

        var snapshot = coordinator.GetSnapshot();

        Assert.Equal(WorkerLifecycleState.Starting, snapshot.State);
        Assert.False(snapshot.IsReady);
        Assert.True(snapshot.IsLive);
        Assert.Null(snapshot.LastError);

        Assert.False(File.Exists(Path.Combine(tempDir, "worker.ready")));
        Assert.True(File.Exists(Path.Combine(tempDir, "worker.live")));
    }

    [Fact]
    public void MarkReady_SetsStateToReady_AndCreatesReadinessFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);

        coordinator.MarkReady();

        var snapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Ready, snapshot.State);
        Assert.True(snapshot.IsReady);
        Assert.True(snapshot.IsLive);

        Assert.True(File.Exists(Path.Combine(tempDir, "worker.ready")));
        Assert.True(File.Exists(Path.Combine(tempDir, "worker.live")));
    }

    [Fact]
    public void MarkStopping_RevokesReadiness_AndRemovesReadinessFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);

        coordinator.MarkReady();
        Assert.True(File.Exists(Path.Combine(tempDir, "worker.ready")));

        coordinator.MarkStopping();

        var snapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Stopping, snapshot.State);
        Assert.False(snapshot.IsReady);
        Assert.True(snapshot.IsLive);

        Assert.False(File.Exists(Path.Combine(tempDir, "worker.ready")));
    }

    [Fact]
    public void MarkFaulted_SetsStateToFaulted_AndRemovesAllHealthFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);

        coordinator.MarkReady();
        coordinator.MarkFaulted(new InvalidOperationException("Fatal worker crash"));

        var snapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Faulted, snapshot.State);
        Assert.False(snapshot.IsReady);
        Assert.False(snapshot.IsLive);
        Assert.Equal("Fatal worker crash", snapshot.LastError);

        Assert.False(File.Exists(Path.Combine(tempDir, "worker.ready")));
        Assert.False(File.Exists(Path.Combine(tempDir, "worker.live")));
    }

    [Fact]
    public void SingleTransientFailure_DoesNotFaultOrDegradeWorker_KeepsReady()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance, degradationThreshold: 3);
        coordinator.MarkReady();

        coordinator.ReportPollFailure(new IOException("Transient network drop"));

        var snapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Ready, snapshot.State);
        Assert.True(snapshot.IsReady);
        Assert.True(snapshot.IsLive);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.Equal("Transient network drop", snapshot.LastError);

        Assert.True(File.Exists(Path.Combine(tempDir, "worker.ready")));
        Assert.True(File.Exists(Path.Combine(tempDir, "worker.live")));
        Assert.False(File.Exists(Path.Combine(tempDir, "worker.degraded")));
    }

    [Fact]
    public void RepeatedTransientFailures_TransitionWorkerToDegraded_RevokesReady_MaintainsLive()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance, degradationThreshold: 3);
        coordinator.MarkReady();

        coordinator.ReportPollFailure(new IOException("Transient error 1"));
        coordinator.ReportPollFailure(new IOException("Transient error 2"));
        Assert.Equal(WorkerLifecycleState.Ready, coordinator.GetSnapshot().State);

        // Third failure reaches threshold of 3 -> transitions to Degraded
        coordinator.ReportPollFailure(new IOException("Transient error 3"));

        var snapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Degraded, snapshot.State);
        Assert.False(snapshot.IsReady);
        Assert.True(snapshot.IsLive); // Liveness maintained!
        Assert.Equal(3, snapshot.ConsecutiveFailures);

        Assert.False(File.Exists(Path.Combine(tempDir, "worker.ready")));
        Assert.True(File.Exists(Path.Combine(tempDir, "worker.degraded")));
        Assert.True(File.Exists(Path.Combine(tempDir, "worker.live")));
    }

    [Fact]
    public void SuccessfulRecovery_RestoresReadyState_AndClearsDegradedFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance, degradationThreshold: 2);
        coordinator.MarkReady();

        coordinator.ReportPollFailure(new IOException("Failure 1"));
        coordinator.ReportPollFailure(new IOException("Failure 2"));
        Assert.Equal(WorkerLifecycleState.Degraded, coordinator.GetSnapshot().State);

        // Recovery
        coordinator.ReportPollSuccess();

        var snapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Ready, snapshot.State);
        Assert.True(snapshot.IsReady);
        Assert.True(snapshot.IsLive);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Null(snapshot.LastError);

        Assert.True(File.Exists(Path.Combine(tempDir, "worker.ready")));
        Assert.True(File.Exists(Path.Combine(tempDir, "worker.live")));
        Assert.False(File.Exists(Path.Combine(tempDir, "worker.degraded")));
    }

    [Fact]
    public void FaultedState_RemainsDistinctFromDegraded_ByLivenessSignal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance, degradationThreshold: 1);
        coordinator.MarkReady();

        // 1. Degraded state: IsReady is false, but IsLive remains true
        coordinator.ReportPollFailure(new TimeoutException("Temporary dependency timeout"));
        var degradedSnapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Degraded, degradedSnapshot.State);
        Assert.False(degradedSnapshot.IsReady);
        Assert.True(degradedSnapshot.IsLive);

        // 2. Faulted state: both IsReady and IsLive are false
        coordinator.MarkFaulted(new OutOfMemoryException("Fatal unrecoverable fault"));
        var faultedSnapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Faulted, faultedSnapshot.State);
        Assert.False(faultedSnapshot.IsReady);
        Assert.False(faultedSnapshot.IsLive);
    }

    [Fact]
    public void Shutdown_TransitionsCorrectlyFromDegradedToStopping()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance, degradationThreshold: 1);
        coordinator.MarkReady();

        coordinator.ReportPollFailure(new IOException("Database busy"));
        Assert.Equal(WorkerLifecycleState.Degraded, coordinator.GetSnapshot().State);
        Assert.True(File.Exists(Path.Combine(tempDir, "worker.degraded")));

        coordinator.MarkStopping();

        var snapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Stopping, snapshot.State);
        Assert.False(snapshot.IsReady);
        Assert.True(snapshot.IsLive);

        Assert.False(File.Exists(Path.Combine(tempDir, "worker.ready")));
        Assert.False(File.Exists(Path.Combine(tempDir, "worker.degraded")));
    }

    [Fact]
    public void WorkerRestart_InitializesStarting_AndBecomesReady()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator1 = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);
        coordinator1.MarkReady();
        coordinator1.MarkStopping();

        // Simulated process restart
        var coordinator2 = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);
        Assert.Equal(WorkerLifecycleState.Starting, coordinator2.GetSnapshot().State);
        Assert.False(coordinator2.GetSnapshot().IsReady);
        Assert.True(coordinator2.GetSnapshot().IsLive);

        coordinator2.MarkReady();
        Assert.Equal(WorkerLifecycleState.Ready, coordinator2.GetSnapshot().State);
        Assert.True(coordinator2.GetSnapshot().IsReady);
    }
}
