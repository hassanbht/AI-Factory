using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Orchestration;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>What one bounded cleanup did (Phase O).</summary>
/// <param name="Outcome">The explicit end state.</param>
/// <param name="Attempts">How many <c>docker rm</c> calls it took.</param>
/// <param name="Elapsed">Wall time spent, bounded by the reconcile window plus one removal attempt.</param>
/// <param name="LastError">The last removal error (single line, truncated), when there was one.</param>
public sealed record ContainerCleanupResult(
    ContainerCleanupOutcome Outcome, int Attempts, TimeSpan Elapsed, string? LastError = null);

/// <summary>
/// Phase O — removal of ONE Factory container, by its deterministic per-run name, with an optional bounded reconciliation.
///
/// Why reconciliation exists: cancelling a run kills the <c>docker run</c> client. If that happens before the Docker
/// daemon has created the container, the daemon can still create it afterwards — the run was dead, but a container
/// (and, for Hermes, its anonymous volume) appeared after the cleanup had already looked and found nothing. A single
/// <c>docker rm -f</c> cannot tell "never existed" from "not created yet" (on Docker 29.8.0 it exits 0 with no output for a
/// missing container), so after an interrupted launch the removal is repeated for a short, bounded window:
/// <list type="number">
/// <item>a removal that prints the name removed the container — done (a name is unique and the launch client is dead);</item>
/// <item>"already in progress" means the daemon is removing it itself — remembered, retried;</item>
/// <item>nothing found — retried every <see cref="DefaultPollInterval"/> until <see cref="DefaultReconcileWindow"/> is spent,
/// then reported as <see cref="ContainerCleanupOutcome.NotFound"/>.</item>
/// </list>
/// Ownership: the only container ever named is <c>ai-factory-agent-&lt;agent-run-id&gt;</c> for this run. It never lists,
/// filters or removes anything else, so a foreign container cannot be touched. A container created later than the window is
/// outside this method's bound; the periodic orphan sweep (<see cref="DockerContainerLifecycleManager.SweepOrphanContainersAsync"/>,
/// run by the Worker every <c>Recovery:PeriodicSweepInterval</c>) removes it, because its AgentRun is terminal. The bound of a
/// cancelled launch is therefore this window plus one sweep interval.
/// </summary>
public static class DockerContainerCleanup
{
    /// <summary>
    /// How long a cleanup keeps looking for a container the daemon may still create. Measured on this host (Docker Desktop,
    /// 16 GB, low free memory) with launches cancelled together: the container was found 1.8-8.5 s after cleanup began with
    /// four launches and 2.8-11.8 s with eight; a fixed 5 s window missed 2-3 of eight. The wait is paid only when NO
    /// container is found (the launch client died before the daemon was asked) — never on the ordinary path, where the first
    /// removal finds the container.
    /// </summary>
    public static readonly TimeSpan DefaultReconcileWindow = TimeSpan.FromSeconds(10);

    /// <summary>The first gap between removals; it doubles every three attempts up to <see cref="MaxPollInterval"/> so a slow daemon is not hammered.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>One <c>docker rm</c> is bounded by its own process timeout, independent of the (already cancelled) run token.</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    private enum Attempt { Removed, Absent, InProgress, Error, Fatal }

    public static async Task<ContainerCleanupResult> RemoveAsync(
        IProcessRunner processRunner,
        string containerName,
        ILogger logger,
        bool reconcile,
        TimeSpan? window = null,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var budget = window ?? DefaultReconcileWindow;
        var interval = pollInterval ?? DefaultPollInterval;
        var clock = timeProvider ?? TimeProvider.System;
        delay ??= (span, token) => Task.Delay(span, clock, token);

        var started = clock.GetTimestamp();
        var attempts = 0;
        var observed = false;
        var lastWasError = false;
        string? lastError = null;

        while (true)
        {
            attempts++;
            var (attempt, error) = await TryRemoveOnceAsync(processRunner, containerName, cancellationToken);
            lastWasError = attempt == Attempt.Error;

            switch (attempt)
            {
                case Attempt.Fatal:
                    // docker is not startable / not permitted: waiting cannot change that.
                    lastError = error;
                    return Finish(ContainerCleanupOutcome.Failed);

                case Attempt.Removed:
                    return Finish(ContainerCleanupOutcome.Removed);

                case Attempt.Absent when observed:
                    // Seen (in progress) a moment ago and gone now: the daemon removed it.
                    return Finish(ContainerCleanupOutcome.Removed);

                case Attempt.Absent when !reconcile:
                    return Finish(ContainerCleanupOutcome.NotFound);

                case Attempt.InProgress:
                    observed = true;
                    break;

                case Attempt.Error:
                    lastError = error;
                    if (!reconcile || cancellationToken.IsCancellationRequested)
                    {
                        return Finish(ContainerCleanupOutcome.Failed);
                    }

                    break;
            }

            if (clock.GetElapsedTime(started) >= budget)
            {
                return Finish(lastWasError ? ContainerCleanupOutcome.Failed : ContainerCleanupOutcome.NotFound);
            }

            try
            {
                await delay(NextInterval(interval, attempts), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Finish(ContainerCleanupOutcome.Failed, "Cleanup was abandoned before the window ended.");
            }
        }

        ContainerCleanupResult Finish(ContainerCleanupOutcome outcome, string? error = null)
        {
            var elapsed = clock.GetElapsedTime(started);
            var result = new ContainerCleanupResult(outcome, attempts, elapsed, error ?? lastError);

            // A removal on the first attempt is the ordinary path (debug); anything that needed waiting, or did not
            // converge, is worth an operator's attention.
            var level = outcome == ContainerCleanupOutcome.Failed
                ? LogLevel.Warning
                : reconcile && outcome == ContainerCleanupOutcome.Removed && attempts > 1
                    ? LogLevel.Information
                    : LogLevel.Debug;

            logger.Log(
                level,
                "ContainerCleanup ContainerName={ContainerName} Outcome={Outcome} Reconcile={Reconcile} Attempts={Attempts} ElapsedMs={ElapsedMs} Error={Error}",
                containerName, outcome, reconcile, attempts, (long)elapsed.TotalMilliseconds, result.LastError);

            return result;
        }
    }

    private static TimeSpan NextInterval(TimeSpan initial, int attempts)
    {
        var doublings = Math.Min(attempts / 3, 8);
        var next = TimeSpan.FromTicks(initial.Ticks << doublings);
        return next < MaxPollInterval ? next : MaxPollInterval;
    }

    private static async Task<(Attempt Result, string? Error)> TryRemoveOnceAsync(
        IProcessRunner processRunner, string containerName, CancellationToken cancellationToken)
    {
        try
        {
            // Its own token: the run's token is the thing that was cancelled; cleanup must still go through. Only the
            // caller's explicit abandonment (cancellationToken) cuts it short.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(AttemptTimeout + TimeSpan.FromSeconds(1));

            var result = await processRunner.RunAsync(
                new ProcessRunRequest("docker", new[] { "rm", "-f", "-v", containerName }, AttemptTimeout),
                attemptCts.Token);

            if (result.TimedOut)
            {
                return (Attempt.Error, "docker rm timed out.");
            }

            var stderr = result.StandardError ?? string.Empty;

            if (result.ExitCode == 0)
            {
                // `docker rm -f` of a missing container exits 0 with no output; a removed one prints its name.
                return string.IsNullOrWhiteSpace(result.StandardOutput)
                    ? (Attempt.Absent, null)
                    : (Attempt.Removed, null);
            }

            if (stderr.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            {
                return (Attempt.Absent, null);
            }

            if (stderr.Contains("already in progress", StringComparison.OrdinalIgnoreCase))
            {
                return (Attempt.InProgress, null);
            }

            return (Attempt.Error, FirstLine(stderr, $"docker rm exited with code {result.ExitCode}."));
        }
        catch (ProcessLaunchException ex)
        {
            return (Attempt.Fatal, FirstLine(ex.Message, "docker could not be started."));
        }
        catch (SecurityViolationException ex)
        {
            return (Attempt.Fatal, FirstLine(ex.Message, "docker is not permitted by the command policy."));
        }
        catch (OperationCanceledException)
        {
            return (Attempt.Error, "docker rm was cancelled.");
        }
    }

    private static string FirstLine(string text, string fallback)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? fallback : line.Length <= 200 ? line : line[..200];
    }
}
