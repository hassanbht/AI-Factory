using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Resilience;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class BulkheadIsolationTests
{
    private readonly OperationalMetrics _metrics = new();

    [Fact]
    public void GetOrCreate_ReturnsSameInstance_CaseInsensitive()
    {
        var registry = new BulkheadRegistry(metrics: _metrics);
        var b1 = registry.GetOrCreate("Database");
        var b2 = registry.GetOrCreate("database");

        Assert.Same(b1, b2);
        Assert.Equal("Database", b1.Name);
    }

    [Fact]
    public async Task ConcurrencyLimit_Enforced_AllowsUpToMaxConcurrent()
    {
        var registry = new BulkheadRegistry(metrics: _metrics);
        var bulkhead = registry.GetOrCreate("TestBulkhead", new BulkheadProfileOptions
        {
            Enabled = true,
            MaxConcurrentExecutions = 2,
            MaxQueueLength = 5
        });

        using var permit1 = await bulkhead.AcquireAsync();
        using var permit2 = await bulkhead.AcquireAsync();

        var snapshot = bulkhead.GetSnapshot();
        Assert.Equal(2, snapshot.ActiveExecutions);
        Assert.Equal(0, snapshot.QueuedExecutions);
    }

    [Fact]
    public async Task Queuing_Enforced_QueuesUpToMaxQueueLength()
    {
        var registry = new BulkheadRegistry(metrics: _metrics);
        var bulkhead = registry.GetOrCreate("QueuingBulkhead", new BulkheadProfileOptions
        {
            Enabled = true,
            MaxConcurrentExecutions = 1,
            MaxQueueLength = 2
        });

        using var permit1 = await bulkhead.AcquireAsync();

        // Queue first caller
        var t1 = Task.Run(async () =>
        {
            using var p = await bulkhead.AcquireAsync();
            await Task.Delay(50);
        });

        // Queue second caller
        var t2 = Task.Run(async () =>
        {
            using var p = await bulkhead.AcquireAsync();
            await Task.Delay(50);
        });

        // Wait deterministically for both callers to enter the queue
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (bulkhead.GetSnapshot().QueuedExecutions < 2 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(10);
        }

        var snapshot = bulkhead.GetSnapshot();
        Assert.Equal(1, snapshot.ActiveExecutions);
        Assert.Equal(2, snapshot.QueuedExecutions);
    }

    [Fact]
    public async Task Rejection_WhenQueueExceeded_ThrowsBulkheadRejectedException()
    {
        var registry = new BulkheadRegistry(metrics: _metrics);
        var bulkhead = registry.GetOrCreate("RejectBulkhead", new BulkheadProfileOptions
        {
            Enabled = true,
            MaxConcurrentExecutions = 1,
            MaxQueueLength = 1
        });

        using var permit = await bulkhead.AcquireAsync();

        // Queue 1
        var queuedTask = Task.Run(async () =>
        {
            using var p = await bulkhead.AcquireAsync();
            await Task.Delay(100);
        });

        await Task.Delay(20);

        // Queue is full (1/1); 3rd request must be rejected immediately
        var ex = await Assert.ThrowsAsync<BulkheadRejectedException>(async () =>
        {
            await bulkhead.AcquireAsync();
        });

        Assert.Equal("RejectBulkhead", ex.DependencyName);
        Assert.Equal(1, ex.MaxConcurrentExecutions);
        Assert.Equal(1, ex.MaxQueueLength);

        var snapshot = bulkhead.GetSnapshot();
        Assert.True(snapshot.TotalRejections >= 1);
    }

    [Fact]
    public async Task PermitRelease_AllowsQueuedCallerToProceed()
    {
        var registry = new BulkheadRegistry(metrics: _metrics);
        var bulkhead = registry.GetOrCreate("ReleaseBulkhead", new BulkheadProfileOptions
        {
            Enabled = true,
            MaxConcurrentExecutions = 1,
            MaxQueueLength = 1
        });

        var permit1 = await bulkhead.AcquireAsync();
        var acquiredSecond = false;

        var t = Task.Run(async () =>
        {
            using var p2 = await bulkhead.AcquireAsync();
            acquiredSecond = true;
        });

        await Task.Delay(20);
        Assert.False(acquiredSecond);

        permit1.Dispose(); // release first permit

        await t;
        Assert.True(acquiredSecond);
    }

    [Fact]
    public async Task Cancellation_WhileQueued_DecrementsQueuedCount()
    {
        var registry = new BulkheadRegistry(metrics: _metrics);
        var bulkhead = registry.GetOrCreate("CancelBulkhead", new BulkheadProfileOptions
        {
            Enabled = true,
            MaxConcurrentExecutions = 1,
            MaxQueueLength = 2
        });

        using var permit = await bulkhead.AcquireAsync();
        using var cts = new CancellationTokenSource();

        var queuedTask = Task.Run(async () =>
        {
            await bulkhead.AcquireAsync(cts.Token);
        });

        await Task.Delay(20);
        Assert.Equal(1, bulkhead.GetSnapshot().QueuedExecutions);

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await queuedTask);
        Assert.Equal(0, bulkhead.GetSnapshot().QueuedExecutions);
    }

    [Fact]
    public async Task DisabledBulkhead_DoesNotThrottle()
    {
        var registry = new BulkheadRegistry(metrics: _metrics);
        var bulkhead = registry.GetOrCreate("DisabledBulkhead", new BulkheadProfileOptions
        {
            Enabled = false,
            MaxConcurrentExecutions = 1,
            MaxQueueLength = 0
        });

        // Can acquire multiple permits simultaneously without queuing or rejection
        using var p1 = await bulkhead.AcquireAsync();
        using var p2 = await bulkhead.AcquireAsync();
        using var p3 = await bulkhead.AcquireAsync();

        var snapshot = bulkhead.GetSnapshot();
        Assert.Equal(0, snapshot.ActiveExecutions);
    }
}
