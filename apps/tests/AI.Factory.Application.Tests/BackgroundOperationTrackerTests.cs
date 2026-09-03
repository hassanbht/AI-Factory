using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Shutdown;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class BackgroundOperationTrackerTests
{
    [Fact]
    public void Track_RegistersActiveOperation_AndIncrementsActiveCount()
    {
        var tracker = new BackgroundOperationTracker();
        var context = new OperationContext(
            CorrelationId: Guid.NewGuid(),
            OperationId: Guid.NewGuid(),
            ParentOperationId: null,
            OperationName: "TestOp",
            ProjectId: Guid.NewGuid(),
            TaskId: Guid.NewGuid(),
            RunId: Guid.NewGuid(),
            StartedAtUtc: DateTime.UtcNow);

        using var handle = tracker.Track(context);

        Assert.Equal(1, tracker.ActiveCount);
        var activeOps = tracker.GetActiveOperations();
        Assert.Single(activeOps);
        Assert.Equal("TestOp", activeOps[0].OperationName);
        Assert.Equal(context.OperationId, activeOps[0].OperationId);
    }

    [Fact]
    public void Dispose_TrackingHandle_DecrementsActiveCount()
    {
        var tracker = new BackgroundOperationTracker();
        var context = new OperationContext(
            CorrelationId: Guid.NewGuid(),
            OperationId: Guid.NewGuid(),
            ParentOperationId: null,
            OperationName: "TestOp",
            ProjectId: null,
            TaskId: null,
            RunId: null,
            StartedAtUtc: DateTime.UtcNow);

        var handle = tracker.Track(context);
        Assert.Equal(1, tracker.ActiveCount);

        handle.Dispose();
        Assert.Equal(0, tracker.ActiveCount);
        Assert.Empty(tracker.GetActiveOperations());
    }

    [Fact]
    public async Task WaitForDrainAsync_ReturnsTrueImmediately_WhenNoOperations()
    {
        var tracker = new BackgroundOperationTracker();

        var drained = await tracker.WaitForDrainAsync(TimeSpan.FromSeconds(1));
        Assert.True(drained);
    }

    [Fact]
    public async Task WaitForDrainAsync_WaitsAndReturnsTrue_WhenAllOperationsComplete()
    {
        var tracker = new BackgroundOperationTracker();
        var context1 = new OperationContext(Guid.NewGuid(), Guid.NewGuid(), null, "Op1", null, null, null, DateTime.UtcNow);
        var context2 = new OperationContext(Guid.NewGuid(), Guid.NewGuid(), null, "Op2", null, null, null, DateTime.UtcNow);

        var handle1 = tracker.Track(context1);
        var handle2 = tracker.Track(context2);

        var drainTask = tracker.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        Assert.False(drainTask.IsCompleted);

        handle1.Dispose();
        Assert.False(drainTask.IsCompleted);

        handle2.Dispose();
        var drained = await drainTask;
        Assert.True(drained);
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public async Task WaitForDrainAsync_TimesOutAndReturnsFalse_WhenOperationsRemain()
    {
        var tracker = new BackgroundOperationTracker();
        var context = new OperationContext(Guid.NewGuid(), Guid.NewGuid(), null, "Op1", null, null, null, DateTime.UtcNow);

        using var handle = tracker.Track(context);

        var drained = await tracker.WaitForDrainAsync(TimeSpan.FromMilliseconds(50));
        Assert.False(drained);
        Assert.Equal(1, tracker.ActiveCount);
    }
}
