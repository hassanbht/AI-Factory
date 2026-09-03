namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Configuration for graceful application shutdown and operation draining.
/// </summary>
public sealed class ShutdownOptions
{
    public const string SectionName = "Shutdown";

    /// <summary>
    /// Total duration to wait for in-flight operations to complete before terminating.
    /// </summary>
    public int GracefulTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Duration after which cancellation is forcefully triggered on in-flight operations.
    /// Must be less than or equal to GracefulTimeoutSeconds.
    /// </summary>
    public int ForceCancellationAfterSeconds { get; set; } = 25;

    public void Validate()
    {
        var failures = new List<string>();

        if (GracefulTimeoutSeconds <= 0) failures.Add($"{nameof(GracefulTimeoutSeconds)} must be positive.");
        if (ForceCancellationAfterSeconds <= 0) failures.Add($"{nameof(ForceCancellationAfterSeconds)} must be positive.");
        if (ForceCancellationAfterSeconds > GracefulTimeoutSeconds)
        {
            failures.Add($"{nameof(ForceCancellationAfterSeconds)} cannot be greater than {nameof(GracefulTimeoutSeconds)}.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Invalid {nameof(ShutdownOptions)}: {string.Join(" ", failures)}");
        }
    }
}
