using AI.Factory.Application.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Diagnostics;

public sealed class OperationalMetricsContractTests
{
    [Fact]
    public void WorkerLifecycleCounters_IncrementCoherently()
    {
        var metrics = new OperationalMetrics();

        var initial = metrics.GetSnapshot();
        Assert.Equal(0, initial.WorkerDegraded);
        Assert.Equal(0, initial.WorkerRecovered);
        Assert.Equal(0, initial.WorkerFaulted);
        Assert.Equal(0, initial.WorkerStopping);
        Assert.Equal(0, initial.WorkerStopped);
        Assert.Equal(0, initial.WorkerPollFailures);

        metrics.RecordWorkerPollFailure();
        metrics.RecordWorkerPollFailure();
        metrics.RecordWorkerDegraded();

        var degradedSnapshot = metrics.GetSnapshot();
        Assert.Equal(2, degradedSnapshot.WorkerPollFailures);
        Assert.Equal(1, degradedSnapshot.WorkerDegraded);
        Assert.Equal(0, degradedSnapshot.WorkerRecovered);

        metrics.RecordWorkerRecovered();

        var recoveredSnapshot = metrics.GetSnapshot();
        Assert.Equal(1, recoveredSnapshot.WorkerDegraded);
        Assert.Equal(1, recoveredSnapshot.WorkerRecovered);

        metrics.RecordWorkerFaulted();
        var faultedSnapshot = metrics.GetSnapshot();
        Assert.Equal(1, faultedSnapshot.WorkerFaulted);

        metrics.RecordWorkerStopping();
        metrics.RecordWorkerStopped();
        var stoppedSnapshot = metrics.GetSnapshot();
        Assert.Equal(1, stoppedSnapshot.WorkerStopping);
        Assert.Equal(1, stoppedSnapshot.WorkerStopped);
    }

    [Fact]
    public void CircuitBreakerCounters_IncrementOnTransitions()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordCircuitOpened();
        metrics.RecordCircuitHalfOpen();
        metrics.RecordCircuitClosed();
        metrics.RecordCircuitRejected();

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(1, snapshot.CircuitOpened);
        Assert.Equal(1, snapshot.CircuitHalfOpen);
        Assert.Equal(1, snapshot.CircuitClosed);
        Assert.Equal(1, snapshot.CircuitRejected);
    }

    [Fact]
    public void BackupAndRestoreCounters_IncrementAccurately()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordBackupSucceeded();
        metrics.RecordBackupValidated();
        metrics.RecordBackupFailed();

        metrics.RecordRestoreSucceeded();
        metrics.RecordRestoreFailed();

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(1, snapshot.BackupSucceeded);
        Assert.Equal(1, snapshot.BackupValidated);
        Assert.Equal(1, snapshot.BackupFailed);
        Assert.Equal(1, snapshot.RestoreSucceeded);
        Assert.Equal(1, snapshot.RestoreFailed);
    }
}
