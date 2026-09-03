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
        var coordinator = new WorkerHealthCoordinator(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);

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
        var coordinator = new WorkerHealthCoordinator(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);

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
        var coordinator = new WorkerHealthCoordinator(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);

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
        var coordinator = new WorkerHealthCoordinator(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);

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
}
