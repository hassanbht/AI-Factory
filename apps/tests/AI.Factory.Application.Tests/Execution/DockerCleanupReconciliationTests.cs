using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase O — the Docker cancel-before-container-creation race, modelled deterministically.
///
/// The race (found in Phase N, DockerLifecycleIntegrationTests.Cancellation_…): the caller cancels, the `docker run` client is
/// killed, cleanup runs `docker rm -f` and finds nothing — then the Docker daemon, which had already been asked, creates the
/// container. The fake daemon below does exactly that on a manual clock: <c>cancel → delayed container appearance → cleanup
/// reconciliation</c>. A real-Docker version of the same race lives in <c>DockerLifecycleIntegrationTests</c>.
///
/// Cleanup must be ownership-scoped: every command issued here is checked to be `docker rm -f -v` of exactly this run's
/// container name, and a foreign container in the same fake daemon must survive untouched.
/// </summary>
public sealed class DockerCleanupReconciliationTests
{
    private static readonly TimeSpan Window = DockerContainerCleanup.DefaultReconcileWindow;
    private static readonly TimeSpan Poll = DockerContainerCleanup.DefaultPollInterval;

    /// <summary>A clock that only moves when the test (the fake daemon, or a delay) moves it.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public TimeSpan Now => TimeSpan.FromTicks(_ticks);

        public void Advance(TimeSpan span) => _ticks += span.Ticks;
    }

    /// <summary>
    /// A Docker daemon reduced to what cleanup can observe: containers by name, creations that land at a given time, the
    /// behaviour of `docker rm -f -v &lt;name&gt;` (exit 0 with the name when it removed one, exit 0 silently when there
    /// is none — as Docker 29.8.0 does), and the cost of every call.
    /// </summary>
    private sealed class FakeDaemon : IProcessRunner
    {
        private readonly ManualTime _time;
        private readonly Dictionary<string, TimeSpan> _pendingCreations = new();

        public FakeDaemon(ManualTime time) => _time = time;

        public HashSet<string> Containers { get; } = new();

        public List<string[]> Commands { get; } = new();

        public TimeSpan CallCost { get; init; } = TimeSpan.FromMilliseconds(40);

        /// <summary>Number of leading `rm` calls that answer "removal ... already in progress" for an existing container.</summary>
        public int InProgressAnswers { get; set; }

        public ProcessRunResult? Fixed { get; init; }

        public Exception? Throws { get; init; }

        public void CreateAt(string name, TimeSpan at) => _pendingCreations[name] = at;

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(new[] { request.FileName }.Concat(request.Arguments).ToArray());

            if (Throws is not null)
            {
                throw Throws;
            }

            _time.Advance(CallCost);

            foreach (var (name, at) in _pendingCreations.Where(p => p.Value <= _time.Now).ToArray())
            {
                Containers.Add(name);
                _pendingCreations.Remove(name);
            }

            if (Fixed is not null)
            {
                return Task.FromResult(Fixed);
            }

            var target = request.Arguments[^1];

            if (Containers.Contains(target) && InProgressAnswers > 0)
            {
                InProgressAnswers--;
                if (InProgressAnswers == 0)
                {
                    Containers.Remove(target); // the daemon's own removal finishes
                }

                return Task.FromResult(new ProcessRunResult(
                    1, string.Empty, $"Error response from daemon: removal of container {target} is already in progress", false));
            }

            return Task.FromResult(Containers.Remove(target)
                ? new ProcessRunResult(0, target + "\n", string.Empty, false)
                : new ProcessRunResult(0, string.Empty, string.Empty, false));
        }
    }

    private static (FakeDaemon Daemon, ManualTime Time, string Name, string Foreign) Scenario(Func<ManualTime, FakeDaemon>? daemon = null)
    {
        var time = new ManualTime();
        var fake = daemon?.Invoke(time) ?? new FakeDaemon(time);
        var name = DockerContainerNaming.GetContainerName(Guid.NewGuid());
        var foreign = "someone-elses-container";
        fake.Containers.Add(foreign);
        return (fake, time, name, foreign);
    }

    private static Task<ContainerCleanupResult> Remove(
        FakeDaemon daemon, ManualTime time, string name, bool reconcile, CancellationToken token = default) =>
        DockerContainerCleanup.RemoveAsync(
            daemon, name, NullLogger.Instance, reconcile,
            delay: (span, _) => { time.Advance(span); return Task.CompletedTask; },
            timeProvider: time,
            cancellationToken: token);

    private static void AssertOnlyEverTouchedThisRunsContainer(FakeDaemon daemon, string name)
    {
        Assert.NotEmpty(daemon.Commands);
        Assert.All(daemon.Commands, command => Assert.Equal(new[] { "docker", "rm", "-f", "-v", name }, command));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    [InlineData(1_200)]
    [InlineData(4_700)]
    [InlineData(9_400)]
    public async Task CancelThenDelayedContainerCreation_ReconciliationRemovesItWithinTheWindow(int daemonCreatesAfterMs)
    {
        var (daemon, time, name, foreign) = Scenario();
        daemon.CreateAt(name, time.Now + TimeSpan.FromMilliseconds(daemonCreatesAfterMs));

        // The cancelled run's first look: nothing there yet, for every case where the daemon is slower than the cleanup.
        var result = await Remove(daemon, time, name, reconcile: true);

        Assert.Equal(ContainerCleanupOutcome.Removed, result.Outcome);
        Assert.DoesNotContain(name, daemon.Containers);
        Assert.Contains(foreign, daemon.Containers);
        Assert.True(result.Elapsed <= Window, $"took {result.Elapsed}");
        AssertOnlyEverTouchedThisRunsContainer(daemon, name);
    }

    [Fact]
    public async Task TheLegacySingleLook_LeaksTheLateContainer_WhichIsWhyReconciliationExists()
    {
        var (daemon, time, name, _) = Scenario();
        daemon.CreateAt(name, time.Now + TimeSpan.FromMilliseconds(300));

        var result = await Remove(daemon, time, name, reconcile: false);

        // One look at t≈40ms finds nothing; the daemon then creates the container and nobody removes it.
        Assert.Equal(ContainerCleanupOutcome.NotFound, result.Outcome);
        Assert.Equal(1, result.Attempts);

        time.Advance(TimeSpan.FromSeconds(1));
        await daemon.RunAsync(new ProcessRunRequest("docker", new[] { "ps" }, TimeSpan.FromSeconds(1))); // lets the daemon "apply" time
        Assert.Contains(name, daemon.Containers);
    }

    [Fact]
    public async Task ContainerThatNeverAppears_EndsNotFound_BoundedByTheWindow_NotOpenEnded()
    {
        var (daemon, time, name, foreign) = Scenario();

        var result = await Remove(daemon, time, name, reconcile: true);

        Assert.Equal(ContainerCleanupOutcome.NotFound, result.Outcome);
        Assert.InRange(result.Elapsed, Window, Window + DockerContainerCleanup.MaxPollInterval + daemon.CallCost + daemon.CallCost);
        Assert.InRange(result.Attempts, 2, (int)(Window / Poll) + 2); // backoff keeps it far below the fixed-interval count
        Assert.Contains(foreign, daemon.Containers);
        AssertOnlyEverTouchedThisRunsContainer(daemon, name);
    }

    [Fact]
    public async Task PollingBacksOff_SoASlowDaemonIsNotHammered()
    {
        var (daemon, time, name, _) = Scenario();

        var result = await Remove(daemon, time, name, reconcile: true);

        // 10 s at a fixed 250 ms would be ~35 `docker rm` calls; with the backoff it is a fraction of that.
        Assert.InRange(result.Attempts, 8, 18);
    }

    [Fact]
    public async Task ContainerCreatedAfterTheWindow_IsOutsideTheBound_AndTheOutcomeSaysNotFound()
    {
        // The documented limit of the bound (the startup orphan sweep is the backstop for anything later).
        var (daemon, time, name, _) = Scenario();
        daemon.CreateAt(name, time.Now + Window + TimeSpan.FromSeconds(3));

        var result = await Remove(daemon, time, name, reconcile: true);

        Assert.Equal(ContainerCleanupOutcome.NotFound, result.Outcome);
        Assert.True(result.Elapsed < Window + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ContainerThatAlreadyExists_IsRemovedOnTheFirstAttempt_WithoutWaiting()
    {
        var (daemon, time, name, foreign) = Scenario();
        daemon.Containers.Add(name);

        var result = await Remove(daemon, time, name, reconcile: true);

        Assert.Equal(ContainerCleanupOutcome.Removed, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.True(result.Elapsed <= daemon.CallCost + TimeSpan.FromMilliseconds(1), "an ordinary cleanup must not wait");
        Assert.Contains(foreign, daemon.Containers);
    }

    [Fact]
    public async Task SingleShotCleanup_NothingThere_ReturnsNotFoundAtOnce_WithoutPolling()
    {
        var (daemon, time, name, _) = Scenario();

        var result = await Remove(daemon, time, name, reconcile: false);

        Assert.Equal(ContainerCleanupOutcome.NotFound, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(daemon.CallCost, time.Now);
    }

    [Fact]
    public async Task RemovalAlreadyInProgress_IsWaitedOut_AndReportedRemoved()
    {
        // `docker run --rm` and our removal overlap: the daemon answers "already in progress", then it is gone.
        var (daemon, time, name, _) = Scenario();
        daemon.Containers.Add(name);
        daemon.InProgressAnswers = 2;

        var result = await Remove(daemon, time, name, reconcile: true);

        Assert.Equal(ContainerCleanupOutcome.Removed, result.Outcome);
        Assert.DoesNotContain(name, daemon.Containers);
        Assert.True(result.Attempts >= 2);
    }

    [Fact]
    public async Task DaemonUnreachable_KeepsTrying_ThenReportsFailed_NeverNotFound()
    {
        var (daemon, time, name, _) = Scenario(t => new FakeDaemon(t)
        {
            Fixed = new ProcessRunResult(1, string.Empty, "error during connect: the docker daemon is not running", false)
        });

        var result = await Remove(daemon, time, name, reconcile: true);

        Assert.Equal(ContainerCleanupOutcome.Failed, result.Outcome);
        Assert.Contains("docker daemon", result.LastError);
        Assert.True(result.Elapsed <= Window + DockerContainerCleanup.MaxPollInterval + daemon.CallCost + daemon.CallCost);
    }

    [Fact]
    public async Task DockerNotInstalled_FailsImmediately_WithoutWaitingTheWindow()
    {
        var (daemon, time, name, _) = Scenario(t => new FakeDaemon(t)
        {
            Throws = new ProcessLaunchException("Failed to start process 'docker'", new InvalidOperationException("not found"))
        });

        var result = await Remove(daemon, time, name, reconcile: true);

        Assert.Equal(ContainerCleanupOutcome.Failed, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(TimeSpan.Zero, time.Now);
    }

    [Fact]
    public async Task CallerAbandoningTheCleanup_EndsItPromptly_AsFailed_NotHung()
    {
        var (daemon, time, name, _) = Scenario();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await Remove(daemon, time, name, reconcile: true, cts.Token);

        Assert.Equal(ContainerCleanupOutcome.Failed, result.Outcome);
        Assert.Empty(daemon.Commands);
    }

    // ------------------------------------------------------------------ the Hermes executor on the same race

    /// <summary>
    /// `docker run` as the executor sees it: the client blocks until the run is cancelled, is then killed (the runner throws),
    /// and only afterwards does the daemon — which had been asked to create the container — create it, a moment later.
    /// </summary>
    private sealed class RacingDockerRunner : IProcessRunner
    {
        private readonly object _gate = new();
        private readonly TimeSpan _daemonLatency;
        private readonly bool _clientTimesOutInstead;

        public RacingDockerRunner(TimeSpan daemonLatency, bool clientTimesOutInstead = false)
        {
            _daemonLatency = daemonLatency;
            _clientTimesOutInstead = clientTimesOutInstead;
            Containers.Add("a-foreign-container");
        }

        public HashSet<string> Containers { get; } = new();

        public List<string[]> Commands { get; } = new();

        public string? RunContainerName { get; private set; }

        private readonly TaskCompletionSource _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the `docker run` client has been started — the moment the test may cancel.</summary>
        public Task Launched => _launched.Task;

        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Commands.Add(new[] { request.FileName }.Concat(request.Arguments).ToArray());
            }

            if (request.Arguments[0] == "run")
            {
                var name = request.Arguments[request.Arguments.ToList().IndexOf("--name") + 1];
                RunContainerName = name;
                _launched.TrySetResult();

                // The daemon has been asked; it will create the container `_daemonLatency` after the request was sent.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(_daemonLatency);
                    lock (_gate)
                    {
                        Containers.Add(name);
                    }
                });

                if (_clientTimesOutInstead)
                {
                    return new ProcessRunResult(-1, string.Empty, string.Empty, TimedOut: true);
                }

                await Task.Delay(Timeout.Infinite, cancellationToken); // ends with the cancellation that kills the client
                return new ProcessRunResult(-1, string.Empty, string.Empty, true);
            }

            var target = request.Arguments[^1];
            lock (_gate)
            {
                return Containers.Remove(target)
                    ? new ProcessRunResult(0, target + "\n", string.Empty, false)
                    : new ProcessRunResult(0, string.Empty, string.Empty, false);
            }
        }
    }

    private static HermesAgentExecutor Executor(IProcessRunner runner, string workspace) => new(
        new HermesOptions
        {
            DockerImage = "test/image:latest",
            WorkspaceRoot = workspace,
            TimeoutSeconds = 60,
            NetworkMode = "bridge"
        },
        runner,
        NullLogger<HermesAgentExecutor>.Instance);

    private static AgentExecutionRequest Request(Guid runId, string workspace) => new(
        "coder", "test-model", "do the task", workspace, AgentRunId: runId);

    [Theory]
    [InlineData(200)]
    [InlineData(700)]
    [InlineData(1_500)]
    public async Task HermesExecutor_CancelledBeforeTheDaemonCreatesTheContainer_ConvergesToZeroFactoryContainers(int daemonLatencyMs)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aif-o-race-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var runner = new RacingDockerRunner(TimeSpan.FromMilliseconds(daemonLatencyMs));
            var runId = Guid.NewGuid();
            using var cts = new CancellationTokenSource();

            var execution = Executor(runner, workspace).ExecuteAsync(Request(runId, workspace), cts.Token);
            await runner.Launched.WaitAsync(TimeSpan.FromSeconds(30)); // cancelled right after the client started, before the daemon is done
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

            // By the time the executor returned control, nothing of this run may be left — not even the late creation.
            await Task.Delay(daemonLatencyMs + 300);
            Assert.Equal(DockerContainerNaming.GetContainerName(runId), runner.RunContainerName);
            Assert.DoesNotContain(runner.RunContainerName!, runner.Containers);
            Assert.Contains("a-foreign-container", runner.Containers);

            var removals = runner.Commands.Where(c => c[1] == "rm").ToList();
            Assert.NotEmpty(removals);
            Assert.All(removals, c => Assert.Equal(new[] { "docker", "rm", "-f", "-v", runner.RunContainerName! }, c));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task HermesExecutor_LaunchClientTimedOutBeforeTheDaemonCreatesTheContainer_ConvergesToZero_AndReportsTimedOut()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aif-o-race-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var runner = new RacingDockerRunner(TimeSpan.FromMilliseconds(600), clientTimesOutInstead: true);
            var runId = Guid.NewGuid();

            var result = await Executor(runner, workspace).ExecuteAsync(Request(runId, workspace));

            Assert.False(result.Success);
            Assert.True(result.TimedOut);

            await Task.Delay(900); // past the daemon's 600 ms: a late creation would have landed by now
            Assert.DoesNotContain(DockerContainerNaming.GetContainerName(runId), runner.Containers);
            Assert.Contains("a-foreign-container", runner.Containers);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task HermesExecutor_CancelledBeforeAnyLaunch_NeverStartsDockerAtAll_SoThereIsNothingToReconcile()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aif-o-race-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var runner = new RacingDockerRunner(TimeSpan.FromMilliseconds(10));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Executor(runner, workspace).ExecuteAsync(Request(Guid.NewGuid(), workspace), cts.Token));

            Assert.DoesNotContain(runner.Commands, c => c[1] == "run");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
