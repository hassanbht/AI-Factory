using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class DatabaseResiliencePolicyTests
{
    private readonly OperationalMetrics _metrics = new();

    private DatabaseResiliencePolicy CreatePolicy(int maxAttempts = 3)
    {
        var options = new DatabaseResilienceOptions
        {
            EnableRetries = true,
            MaxRetryAttempts = maxAttempts,
            InitialRetryDelay = TimeSpan.FromMilliseconds(5),
            MaxRetryDelay = TimeSpan.FromMilliseconds(20)
        };

        return new DatabaseResiliencePolicy(
            options,
            _metrics,
            NullLogger<DatabaseResiliencePolicy>.Instance);
    }

    [Fact]
    public void Classify_SqliteBusy_ClassifiesAsBusyAndTransient()
    {
        var policy = CreatePolicy();
        var ex = new SqliteException("database is locked", 5);

        Assert.Equal(DatabaseFailureClassification.Busy, policy.Classify(ex));
        Assert.True(policy.IsTransient(ex));
    }

    [Fact]
    public void Classify_SqliteLocked_ClassifiesAsLockedAndTransient()
    {
        var policy = CreatePolicy();
        var ex = new SqliteException("database table is locked", 6);

        Assert.Equal(DatabaseFailureClassification.Locked, policy.Classify(ex));
        Assert.True(policy.IsTransient(ex));
    }

    [Fact]
    public void Classify_ConcurrencyConflict_ClassifiesAsConcurrencyAndNonTransient()
    {
        var policy = CreatePolicy();
        var ex = new ConcurrencyConflictException();

        Assert.Equal(DatabaseFailureClassification.Concurrency, policy.Classify(ex));
        Assert.False(policy.IsTransient(ex));
        Assert.True(policy.IsConcurrencyConflict(ex));
    }

    [Fact]
    public async Task ExecuteAsync_TransientFailure_RetriesAndSucceeds()
    {
        var policy = CreatePolicy(maxAttempts: 3);
        var attempts = 0;

        var result = await policy.ExecuteAsync("TestQuery", ct =>
        {
            attempts++;
            if (attempts < 2)
            {
                throw new SqliteException("database is locked", 5); // Busy transient error
            }

            return Task.FromResult("Success");
        });

        Assert.Equal("Success", result);
        Assert.Equal(2, attempts);

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(1, snapshot.DatabaseOperations);
        Assert.Equal(0, snapshot.DatabaseFailures);
        Assert.Equal(1, snapshot.DatabaseRetries);
        Assert.Equal(1, snapshot.DatabaseBusyFailures);
    }

    [Fact]
    public async Task ExecuteAsync_ExhaustedRetries_ThrowsAndRecordsFailure()
    {
        var policy = CreatePolicy(maxAttempts: 2);
        var attempts = 0;

        await Assert.ThrowsAsync<SqliteException>(() =>
            policy.ExecuteAsync<string>("ExhaustingQuery", ct =>
            {
                attempts++;
                throw new SqliteException("database is locked", 5);
            }));

        Assert.Equal(2, attempts);

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(1, snapshot.DatabaseOperations);
        Assert.Equal(1, snapshot.DatabaseFailures);
        Assert.Equal(1, snapshot.DatabaseRetries);
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientError_ThrowsImmediatelyWithoutRetries()
    {
        var policy = CreatePolicy(maxAttempts: 3);
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.ExecuteAsync<string>("NonTransientQuery", ct =>
            {
                attempts++;
                throw new InvalidOperationException("Non-transient business exception");
            }));

        Assert.Equal(1, attempts);

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(0, snapshot.DatabaseRetries);
        Assert.Equal(1, snapshot.DatabaseFailures);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsCancellationToken()
    {
        var policy = CreatePolicy(maxAttempts: 3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.ExecuteAsync<string>("CancelledQuery", ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult("NeverReached");
            }, cts.Token));
    }
}
