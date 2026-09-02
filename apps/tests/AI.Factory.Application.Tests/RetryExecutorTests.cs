using AI.Factory.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class RetryExecutorTests
{
    private sealed class TransientException : Exception
    {
        public TransientException(string message) : base(message)
        {
        }
    }

    private static RetryExecutor MakeExecutor(int maxAttempts = 3) =>
        new(
            new RetryOptions { MaxAttempts = maxAttempts, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) },
            NullLogger<RetryExecutor>.Instance);

    [Fact]
    public async Task ExecuteAsync_RetriesTransientFailure_ThenSucceeds()
    {
        var callCount = 0;
        var executor = MakeExecutor();

        var result = await executor.ExecuteAsync(
            "TestOp",
            _ =>
            {
                callCount++;
                if (callCount < 3) throw new TransientException("temporary");
                return Task.FromResult(42);
            },
            _ => true,
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetry_WhenClassifierMarksNonTransient()
    {
        var callCount = 0;
        var executor = MakeExecutor();

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync<int>(
            "TestOp",
            _ =>
            {
                callCount++;
                throw new InvalidOperationException("business rule violated");
            },
            _ => false,
            CancellationToken.None));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesFailure_WhenMaxAttemptsReached()
    {
        var callCount = 0;
        var executor = MakeExecutor(maxAttempts: 3);

        await Assert.ThrowsAsync<TransientException>(() => executor.ExecuteAsync<int>(
            "TestOp",
            _ =>
            {
                callCount++;
                throw new TransientException("still failing");
            },
            _ => true,
            CancellationToken.None));

        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation_DuringRetryDelay()
    {
        using var cts = new CancellationTokenSource();
        var executor = new RetryExecutor(
            new RetryOptions { MaxAttempts = 5, BaseDelay = TimeSpan.FromSeconds(30) },
            NullLogger<RetryExecutor>.Instance);

        var task = executor.ExecuteAsync<int>(
            "TestOp",
            _ => throw new TransientException("temporary"),
            _ => true,
            cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(422, false)]
    public void HttpTransientClassifier_ClassifiesStatusCodes_Correctly(int statusCode, bool expectedTransient)
    {
        var ex = new HttpRequestException("failed", null, (System.Net.HttpStatusCode)statusCode);
        Assert.Equal(expectedTransient, HttpTransientClassifier.IsTransient(ex));
    }

    [Fact]
    public void HttpTransientClassifier_TreatsConnectionLevelFailure_AsTransient()
    {
        var ex = new HttpRequestException("connection refused");
        Assert.True(HttpTransientClassifier.IsTransient(ex));
    }

    [Fact]
    public void HttpTransientClassifier_TreatsGenericException_AsNonTransient()
    {
        Assert.False(HttpTransientClassifier.IsTransient(new InvalidOperationException("business failure")));
    }
}
