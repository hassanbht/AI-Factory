using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Shutdown;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ApplicationShutdownCoordinatorTests
{
    [Fact]
    public void IsStopping_InitiallyFalse()
    {
        using var coordinator = new ApplicationShutdownCoordinator();

        Assert.False(coordinator.IsStopping);
        Assert.False(coordinator.StoppingToken.IsCancellationRequested);
    }

    [Fact]
    public void TryBeginShutdown_FlipsIsStopping_AndCancelsStoppingToken()
    {
        using var coordinator = new ApplicationShutdownCoordinator();

        var result = coordinator.TryBeginShutdown();

        Assert.True(result);
        Assert.True(coordinator.IsStopping);
        Assert.True(coordinator.StoppingToken.IsCancellationRequested);
    }

    [Fact]
    public void TryBeginShutdown_IsIdempotent_SecondCallReturnsFalse()
    {
        using var coordinator = new ApplicationShutdownCoordinator();

        var first = coordinator.TryBeginShutdown();
        var second = coordinator.TryBeginShutdown();

        Assert.True(first);
        Assert.False(second);
        Assert.True(coordinator.IsStopping);
    }

    [Fact]
    public async Task WaitForShutdownAsync_CompletesWhenShutdownInitiated()
    {
        using var coordinator = new ApplicationShutdownCoordinator();

        var waitTask = coordinator.WaitForShutdownAsync();
        Assert.False(waitTask.IsCompleted);

        coordinator.TryBeginShutdown();

        await waitTask;
        Assert.True(waitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitForShutdownAsync_CompletesImmediately_IfAlreadyStopping()
    {
        using var coordinator = new ApplicationShutdownCoordinator();
        coordinator.TryBeginShutdown();

        var waitTask = coordinator.WaitForShutdownAsync();
        await waitTask;

        Assert.True(waitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void HostApplicationLifetime_TriggersShutdown()
    {
        var testLifetime = new TestHostApplicationLifetime();
        using var coordinator = new ApplicationShutdownCoordinator(hostLifetime: testLifetime);

        Assert.False(coordinator.IsStopping);

        testLifetime.TriggerStopping();

        Assert.True(coordinator.IsStopping);
        Assert.True(coordinator.StoppingToken.IsCancellationRequested);
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _startedCts = new();
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();

        public CancellationToken ApplicationStarted => _startedCts.Token;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped => _stoppedCts.Token;

        public void StopApplication() => _stoppingCts.Cancel();

        public void TriggerStopping() => _stoppingCts.Cancel();
    }
}
