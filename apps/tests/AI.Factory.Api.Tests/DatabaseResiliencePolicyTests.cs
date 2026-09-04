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

    // ---------------------------------------------------------------
    // Phase 17.4.12 Workstream C — deterministic SQLITE_LOCKED coverage
    // mirroring the existing SQLITE_BUSY coverage above. Both exercise the
    // real DatabaseResiliencePolicy.ExecuteAsync retry/backoff decision
    // logic directly against a real, directly-constructed SqliteException —
    // no fake provider, no mocked DbContext.
    // ---------------------------------------------------------------

    [Fact]
    public void Classify_SqliteLocked_IsNeverConfusedWithBusy()
    {
        var policy = CreatePolicy();

        Assert.Equal(DatabaseFailureClassification.Locked, policy.Classify(new SqliteException("database table is locked", 6)));
        Assert.Equal(DatabaseFailureClassification.Busy, policy.Classify(new SqliteException("database is locked", 5)));
    }

    [Fact]
    public async Task ExecuteAsync_Locked_RetriesAndSucceeds()
    {
        var policy = CreatePolicy(maxAttempts: 3);
        var attempts = 0;

        var result = await policy.ExecuteAsync("TestQuery", ct =>
        {
            attempts++;
            if (attempts < 2)
            {
                throw new SqliteException("database table is locked", 6); // Locked transient error
            }

            return Task.FromResult("Success");
        });

        Assert.Equal("Success", result);
        Assert.Equal(2, attempts);

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(1, snapshot.DatabaseOperations);
        Assert.Equal(0, snapshot.DatabaseFailures);
        Assert.Equal(1, snapshot.DatabaseRetries);
        Assert.Equal(1, snapshot.DatabaseLockedFailures);
    }

    [Fact]
    public async Task ExecuteAsync_Locked_ExhaustedRetries_ThrowsAndRecordsFailure()
    {
        var policy = CreatePolicy(maxAttempts: 2);
        var attempts = 0;

        await Assert.ThrowsAsync<SqliteException>(() =>
            policy.ExecuteAsync<string>("ExhaustingQuery", ct =>
            {
                attempts++;
                throw new SqliteException("database table is locked", 6);
            }));

        Assert.Equal(2, attempts);

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(1, snapshot.DatabaseOperations);
        Assert.Equal(1, snapshot.DatabaseFailures);
        Assert.Equal(1, snapshot.DatabaseRetries);
        // Recorded once per failed attempt (retryable attempt + the final exhausted attempt).
        Assert.Equal(2, snapshot.DatabaseLockedFailures);
    }

    [Fact]
    public async Task ExecuteAsync_Busy_BackoffDelayGrows_AcrossRetries()
    {
        // Proves the exponential backoff calculation actually runs between attempts —
        // not just that a retry happens, but that ComputeDelay's real production output
        // is what ExecuteAsync awaits on. Bounds are loose (accounting for the policy's
        // documented ±20% jitter) specifically to avoid flakiness while still failing if
        // backoff were skipped entirely (elapsed would be ~0ms instead of >= the floor).
        var options = new DatabaseResilienceOptions
        {
            EnableRetries = true,
            MaxRetryAttempts = 3,
            InitialRetryDelay = TimeSpan.FromMilliseconds(40),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200),
        };
        var policy = new DatabaseResiliencePolicy(options, _metrics, NullLogger<DatabaseResiliencePolicy>.Instance);
        var attempts = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await policy.ExecuteAsync("TestQuery", ct =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new SqliteException("database is locked", 5);
            }

            return Task.FromResult("Success");
        });
        sw.Stop();

        Assert.Equal("Success", result);
        Assert.Equal(3, attempts);

        // Two backoff waits occurred: ~40ms then ~80ms (exponential), each with up to -20%
        // jitter — a floor of 90ms total is comfortably below the ~120ms expected minimum
        // while still proving backoff wasn't skipped (skipped backoff would be ~0ms).
        Assert.True(sw.ElapsedMilliseconds >= 90, $"Expected backoff delays to have elapsed, but only {sw.ElapsedMilliseconds}ms passed.");
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientDbException_IsNotClassifiedAsBusyOrLocked_AndDoesNotRetry()
    {
        var policy = CreatePolicy(maxAttempts: 3);
        var nonTransient = new NonTransientDbException("constraint violation");

        var classification = policy.Classify(nonTransient);
        Assert.NotEqual(DatabaseFailureClassification.Busy, classification);
        Assert.NotEqual(DatabaseFailureClassification.Locked, classification);
        Assert.False(policy.IsTransient(nonTransient));

        var attempts = 0;
        await Assert.ThrowsAsync<NonTransientDbException>(() =>
            policy.ExecuteAsync<string>("NonTransientDbQuery", ct =>
            {
                attempts++;
                throw nonTransient;
            }));

        Assert.Equal(1, attempts);
        Assert.Equal(0, _metrics.GetSnapshot().DatabaseRetries);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledDuringBackoffDelay_StopsImmediately_NoFurtherAttempt()
    {
        var options = new DatabaseResilienceOptions
        {
            EnableRetries = true,
            MaxRetryAttempts = 5,
            InitialRetryDelay = TimeSpan.FromMilliseconds(500), // comfortably longer than the cancel-after window below
            MaxRetryDelay = TimeSpan.FromSeconds(2),
        };
        var policy = new DatabaseResiliencePolicy(options, _metrics, NullLogger<DatabaseResiliencePolicy>.Instance);
        var attempts = 0;

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.ExecuteAsync<string>("CancelledDuringBackoff", ct =>
            {
                attempts++;
                throw new SqliteException("database is locked", 5);
            }, cts.Token));

        // Only the first attempt ran — cancellation fired while awaiting the backoff delay
        // before a second attempt could start.
        Assert.Equal(1, attempts);
    }

    private sealed class NonTransientDbException : System.Data.Common.DbException
    {
        public NonTransientDbException(string message) : base(message)
        {
        }

        public override bool IsTransient => false;
    }
}
