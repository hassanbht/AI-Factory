using AI.Factory.Application.Orchestration;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class TimeoutGuardTests
{
    private static async Task<int> SlowOperationAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        return 1;
    }

    [Fact]
    public async Task RunAsync_ReturnsResult_WhenOperationCompletesBeforeTimeout()
    {
        var result = await TimeoutGuard.RunAsync(
            _ => Task.FromResult(42),
            TimeSpan.FromSeconds(5),
            "FastOp",
            CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsync_ThrowsTimeoutException_WhenOperationExceedsTimeout()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            TimeoutGuard.RunAsync(
                SlowOperationAsync,
                TimeSpan.FromMilliseconds(20),
                "SlowOp",
                CancellationToken.None));

        Assert.Contains("SlowOp", ex.Message);
    }

    [Fact]
    public async Task RunAsync_PropagatesOperationCanceledException_WhenCallerCancels_NotTimeoutException()
    {
        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TimeoutGuard.RunAsync(
                SlowOperationAsync,
                TimeSpan.FromSeconds(30),
                "CancelledOp",
                callerCts.Token));

        Assert.IsNotType<TimeoutException>(ex);
    }
}
