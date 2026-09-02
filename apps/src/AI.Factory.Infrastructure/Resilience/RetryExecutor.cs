using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Resilience;

/// <summary>
/// Retries a transient failure with exponential backoff + jitter. Never
/// retries the caller's own cancellation, never retries an exception the
/// caller's classifier marks non-transient, and lets the final attempt's
/// exception propagate unchanged once attempts are exhausted — so a caller
/// wrapping this never has to distinguish "gave up" from "never going to
/// succeed" beyond the exception it already knows how to handle.
/// </summary>
public sealed class RetryExecutor
{
    private readonly RetryOptions _options;
    private readonly ILogger<RetryExecutor> _logger;

    public RetryExecutor(RetryOptions options, ILogger<RetryExecutor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken = default)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;

            try
            {
                return await action(cancellationToken);
            }
            catch (Exception ex) when (
                attempt < _options.MaxAttempts
                && !cancellationToken.IsCancellationRequested
                && isTransient(ex))
            {
                var delay = ComputeDelay(attempt);

                _logger.LogWarning(
                    "Retry attempt: Operation={Operation}, Attempt={Attempt}, Reason={Reason}, Delay={Delay}",
                    operation, attempt, ex.Message, delay);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        var exponential = _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, _options.MaxDelay.TotalMilliseconds);
        var jitterRange = capped * _options.JitterFactor;
        var jitter = (Random.Shared.NextDouble() * 2 - 1) * jitterRange;

        return TimeSpan.FromMilliseconds(Math.Max(0, capped + jitter));
    }
}
