using AI.Factory.Application.Resilience;
using Xunit;

namespace AI.Factory.Application.Tests.Stress;

/// <summary>
/// Phase 17.4.9 — bulkhead permit-release stress coverage beyond the basic
/// concurrency/queueing/rejection/cancellation cases already covered in
/// <c>BulkheadIsolationTests</c>: release-on-exception, release-on-timeout,
/// release-on-cancel-while-held, repeated mixed-outcome cycles under
/// concurrent load (semaphore leak detection), and deterministic queue
/// overflow under true concurrency.
/// </summary>
[Trait("Category", "Stress")]
public sealed class BulkheadStressTests
{
    private static IBulkhead MakeBulkhead(string name, int maxConcurrent = 3, int maxQueue = 5)
    {
        var registry = new BulkheadRegistry();
        return registry.GetOrCreate(name, new BulkheadProfileOptions
        {
            Enabled = true,
            MaxConcurrentExecutions = maxConcurrent,
            MaxQueueLength = maxQueue
        });
    }

    [Fact]
    public async Task PermitRelease_AfterExceptionInsideUsing_ReturnsToZeroAndAllowsReacquire()
    {
        var bulkhead = MakeBulkhead("ExceptionRelease", maxConcurrent: 1, maxQueue: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var permit = await bulkhead.AcquireAsync();
            throw new InvalidOperationException("simulated failure inside guarded work");
        });

        Assert.Equal(0, bulkhead.GetSnapshot().ActiveExecutions);

        using var next = await bulkhead.AcquireAsync();
        Assert.Equal(1, bulkhead.GetSnapshot().ActiveExecutions);
    }

    [Fact]
    public async Task PermitRelease_AfterHeldOperationTimesOut_ReturnsToZero()
    {
        var bulkhead = MakeBulkhead("TimeoutRelease", maxConcurrent: 1, maxQueue: 1);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var permit = await bulkhead.AcquireAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        });

        Assert.Equal(0, bulkhead.GetSnapshot().ActiveExecutions);

        using var next = await bulkhead.AcquireAsync();
        Assert.Equal(1, bulkhead.GetSnapshot().ActiveExecutions);
    }

    [Fact]
    public async Task PermitRelease_AfterCancellationWhilePermitAlreadyHeld_ReturnsToZero()
    {
        var bulkhead = MakeBulkhead("CancelWhileHeldRelease", maxConcurrent: 1, maxQueue: 1);
        using var cts = new CancellationTokenSource();

        var permit = await bulkhead.AcquireAsync();
        Assert.Equal(1, bulkhead.GetSnapshot().ActiveExecutions);

        try
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // Expected — permit was already held before cancellation fired.
        }
        finally
        {
            permit.Dispose();
        }

        var snapshot = bulkhead.GetSnapshot();
        Assert.Equal(0, snapshot.ActiveExecutions);
        Assert.Equal(0, snapshot.QueuedExecutions);
    }

    [Fact]
    public async Task RepeatedMixedCycles_UnderConcurrentLoad_NoPermitOrQueueLeak()
    {
        var bulkhead = MakeBulkhead("MixedCycleStress", maxConcurrent: 3, maxQueue: 5);

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource();
            try
            {
                using var permit = await bulkhead.AcquireAsync(cts.Token);
                await Task.Delay(5);

                switch (i % 3)
                {
                    case 0:
                        throw new InvalidOperationException("scripted cycle failure");
                    case 1:
                        cts.Cancel();
                        cts.Token.ThrowIfCancellationRequested();
                        break;
                    default:
                        break; // success path, permit released via using
                }
            }
            catch (BulkheadRejectedException)
            {
                // Expected under contention — capacity intentionally small.
            }
            catch (InvalidOperationException)
            {
                // Expected scripted failure.
            }
            catch (OperationCanceledException)
            {
                // Expected scripted cancellation.
            }
        }));

        await Task.WhenAll(tasks);

        var snapshot = bulkhead.GetSnapshot();
        Assert.Equal(0, snapshot.ActiveExecutions);
        Assert.Equal(0, snapshot.QueuedExecutions);
    }

    [Fact]
    public async Task ConcurrentQueueOverflow_RejectsExactlyExpectedCount()
    {
        var bulkhead = MakeBulkhead("ConcurrentOverflow", maxConcurrent: 2, maxQueue: 2);

        // Hold both active permits before firing the remaining callers, so
        // queue occupancy and rejection counts are deterministic.
        var permit1 = await bulkhead.AcquireAsync();
        var permit2 = await bulkhead.AcquireAsync();

        var release = new TaskCompletionSource();
        var rejected = 0;
        var queuedSucceeded = 0;

        // Queue capacity is enforced via an atomic counter (see BulkheadRegistry),
        // so which 2 of the 8 concurrent callers win the queue slots is racy,
        // but the total accepted (2) vs. rejected (6) split is deterministic.
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                using var permit = await bulkhead.AcquireAsync();
                await release.Task;
                Interlocked.Increment(ref queuedSucceeded);
            }
            catch (BulkheadRejectedException)
            {
                Interlocked.Increment(ref rejected);
            }
        })).ToArray();

        // Wait until all 8 callers have either entered the queue (2) or been rejected (6),
        // rather than guessing with an arbitrary sleep that races under threadpool load.
        var spinTimeout = TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < spinTimeout && (bulkhead.GetSnapshot().QueuedExecutions < 2 || Volatile.Read(ref rejected) < 6))
        {
            await Task.Yield();
        }

        release.SetResult();
        permit1.Dispose();
        permit2.Dispose();

        await Task.WhenAll(tasks);

        Assert.Equal(6, rejected);
        Assert.Equal(2, queuedSucceeded);
        Assert.Equal(0, bulkhead.GetSnapshot().ActiveExecutions);
        Assert.Equal(0, bulkhead.GetSnapshot().QueuedExecutions);
    }
}
