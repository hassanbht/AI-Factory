namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Runs an operation under a timeout without confusing it with caller-driven
/// cancellation: if the caller's own token fires, OperationCanceledException
/// propagates unchanged (the standard "user cancelled" signal); if only the
/// timeout fires, it is converted into a TimeoutException instead, so a
/// timeout always surfaces as a distinguishable, catchable failure — never
/// as an unhandled cancellation.
/// </summary>
public static class TimeoutGuard
{
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        string operationType,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var startedAtUtc = DateTime.UtcNow;

        try
        {
            return await operation(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var elapsed = DateTime.UtcNow - startedAtUtc;

            throw new TimeoutException(
                $"{operationType} exceeded the configured {timeout} limit (elapsed {elapsed}).");
        }
    }
}
