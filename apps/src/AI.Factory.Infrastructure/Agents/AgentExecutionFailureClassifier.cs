using AI.Factory.Application.Abstractions;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// Phase D.1 — Deterministic failure classification and bounded retry policy.
///
/// Principles:
///   Transient launch failure      → retry
///   Deterministic config failure  → fail (NO retry)
///   Timeout                       → fail (NO retry)
///   Cancellation                  → fail (NO retry)
///   Agent's own non-zero exit     → fail (NO retry)
/// </summary>
public static class AgentExecutionFailureClassifier
{
    /// <summary>
    /// Determines whether an agent execution result represents a genuinely transient launch failure
    /// (e.g. temporary Docker daemon socket connection refused, connection reset by peer).
    /// Returns false for timeouts, cancellations, normal non-zero agent exits, and deterministic
    /// Docker configuration failures (missing network, invalid image, volume mount error, usage errors).
    /// </summary>
    public static bool IsTransientLaunchFailure(AgentExecutionResult result)
    {
        if (result.Success || result.TimedOut || result.Cancelled)
        {
            return false;
        }

        var error = result.Error ?? string.Empty;

        // If it's a genuine transient daemon launch error (e.g. temporary socket timeout/connection reset)
        if (IsGenuineTransientLaunchError(error))
        {
            return true;
        }

        if (IsTimeout(error) || IsCancellation(error))
        {
            return false;
        }

        // If the process ran and returned exit code 1..124, the container started and the agent
        // executed and exited with that non-zero code. Normal agent application failure — never retry.
        if (result.ExitCode.HasValue && result.ExitCode.Value >= 1 && result.ExitCode.Value <= 124)
        {
            return false;
        }

        // Deterministic Docker configuration or CLI errors — never retry.
        if (IsDeterministicConfigurationError(error))
        {
            return false;
        }

        return false;
    }

    public static bool IsTimeout(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || error.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCancellation(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("canceled", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDeterministicConfigurationError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return (error.Contains("network", StringComparison.OrdinalIgnoreCase) && error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            || error.Contains("invalid reference format", StringComparison.OrdinalIgnoreCase)
            || (error.Contains("manifest for", StringComparison.OrdinalIgnoreCase) && error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            || (error.Contains("repository", StringComparison.OrdinalIgnoreCase) && error.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            || (error.Contains("image", StringComparison.OrdinalIgnoreCase) && error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            || error.Contains("invalid mount", StringComparison.OrdinalIgnoreCase)
            || (error.Contains("mount", StringComparison.OrdinalIgnoreCase) && error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            || (error.Contains("volume", StringComparison.OrdinalIgnoreCase) && error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            || error.Contains("duplicate mount", StringComparison.OrdinalIgnoreCase)
            || error.Contains("volume name is invalid", StringComparison.OrdinalIgnoreCase)
            || error.Contains("bind source path does not exist", StringComparison.OrdinalIgnoreCase)
            || error.Contains("unknown flag", StringComparison.OrdinalIgnoreCase)
            || error.Contains("flag provided but not defined", StringComparison.OrdinalIgnoreCase)
            || error.Contains("unknown shorthand flag", StringComparison.OrdinalIgnoreCase)
            || error.Contains("see 'docker run --help'", StringComparison.OrdinalIgnoreCase)
            || error.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || error.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            || error.Contains("security violation", StringComparison.OrdinalIgnoreCase)
            || error.Contains("command policy violation", StringComparison.OrdinalIgnoreCase)
            || error.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase)
            || error.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || error.Contains("is already in use by container", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no longer exists or is not accessible", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGenuineTransientLaunchError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("cannot connect to the docker daemon", StringComparison.OrdinalIgnoreCase)
            || error.Contains("is the docker daemon running", StringComparison.OrdinalIgnoreCase)
            || error.Contains("error during connect", StringComparison.OrdinalIgnoreCase)
            || error.Contains("dial unix /var/run/docker.sock", StringComparison.OrdinalIgnoreCase)
            || error.Contains("pipe/docker_engine", StringComparison.OrdinalIgnoreCase)
            || error.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || error.Contains("connection reset by peer", StringComparison.OrdinalIgnoreCase)
            || error.Contains("unexpected eof", StringComparison.OrdinalIgnoreCase)
            || error.Contains("temporary network timeout connecting to docker daemon", StringComparison.OrdinalIgnoreCase);
    }
}
