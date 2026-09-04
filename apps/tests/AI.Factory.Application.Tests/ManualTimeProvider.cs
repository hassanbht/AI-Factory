namespace AI.Factory.Application.Tests;

/// <summary>
/// Minimal deterministic <see cref="TimeProvider"/> for resilience tests — avoids pulling in
/// Microsoft.Extensions.TimeProvider.Testing for the one thing these tests need: an advanceable
/// UTC clock. Only <see cref="GetUtcNow"/> is overridden; circuit breaker state transitions never
/// use <see cref="TimeProvider.CreateTimer"/> or high-resolution timestamps.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? DateTimeOffset.UtcNow;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
