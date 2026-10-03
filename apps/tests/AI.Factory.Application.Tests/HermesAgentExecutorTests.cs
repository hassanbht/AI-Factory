using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class HermesAgentExecutorTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Func<ProcessRunRequest, ProcessRunResult> _respond;
        private readonly Exception? _throwOnRun;
        private readonly Action<ProcessRunRequest>? _writeReport;

        /// <param name="writeReport">What the scripted Hermes leaves as its run report; defaults to a completed run.</param>
        public FakeProcessRunner(Func<ProcessRunRequest, ProcessRunResult> respond, Action<ProcessRunRequest>? writeReport = null)
        {
            _respond = respond;
            _writeReport = writeReport ?? (r => HermesRunReportFake.Write(r));
        }

        public FakeProcessRunner(Exception throwOnRun)
        {
            _throwOnRun = throwOnRun;
            _respond = _ => throw new InvalidOperationException("unreachable");
        }

        public ProcessRunRequest? LastRequest { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Arguments.Contains("run") || LastRequest is null)
            {
                LastRequest = request;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_throwOnRun is not null)
            {
                throw _throwOnRun;
            }

            var result = _respond(request);
            if (!result.TimedOut)
            {
                _writeReport?.Invoke(request);
            }

            return Task.FromResult(result);
        }
    }

    private static HermesOptions MakeOptions(int maxOutputLength = 100_000)
    {
        return new HermesOptions
        {
            DockerImage = "test/image:latest",
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-hermes-tests"),
            TimeoutSeconds = 60,
            MaxOutputLength = maxOutputLength
        };
    }

    [Fact]
    public async Task ExecuteAsync_UsesControlledEnvironment_AndConfiguredNetworkMode_AndWorkingDirectory()
    {
        var runner = new FakeProcessRunner(_ =>
            new ProcessRunResult(0, "ok", string.Empty, TimedOut: false));

        var options = MakeOptions();
        var executor = new HermesAgentExecutor(options, runner, NullLogger<HermesAgentExecutor>.Instance);

        await executor.ExecuteAsync(new AgentExecutionRequest("coder", null, "do the task", string.Empty));

        Assert.NotNull(runner.LastRequest);
        Assert.Contains("--network", runner.LastRequest!.Arguments);
        Assert.Contains(options.NetworkMode, runner.LastRequest.Arguments);
        Assert.False(string.IsNullOrWhiteSpace(runner.LastRequest.WorkingDirectory));
        Assert.NotNull(runner.LastRequest.EnvironmentVariables);
        Assert.DoesNotContain(
            runner.LastRequest.EnvironmentVariables!.Keys,
            key => key is not "PATH");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSuccess_ForZeroExitCode()
    {
        var runner = new FakeProcessRunner(_ =>
            new ProcessRunResult(0, "all good", string.Empty, TimedOut: false));

        var executor = new HermesAgentExecutor(
            MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            new AgentExecutionRequest("coder", null, "do the task", string.Empty));

        Assert.True(result.Success);
        Assert.Equal("all good", result.Output);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_ForNonZeroExitCode()
    {
        var runner = new FakeProcessRunner(_ =>
            new ProcessRunResult(1, string.Empty, "boom", TimedOut: false));

        var executor = new HermesAgentExecutor(
            MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            new AgentExecutionRequest("coder", null, "do the task", string.Empty));

        Assert.False(result.Success);
        Assert.Equal("boom", result.Error);
    }

    // Phase L — exit 0 alone never proves success: Hermes 0.20.6 writes its run report on every
    // terminal path of `-z`, so a run is successful only with a report saying it completed.
    [Theory]
    [InlineData(null, HermesAgentExecutor.ReportUnavailableError)]                       // report missing
    [InlineData("", HermesAgentExecutor.ReportUnavailableError)]                         // empty
    [InlineData("{\"failed\": fal", HermesAgentExecutor.ReportUnavailableError)]         // malformed
    [InlineData("[]", HermesAgentExecutor.ReportUnavailableError)]                       // not an object
    [InlineData("{\"failed\": false}", HermesAgentExecutor.RunIncompleteError)]          // no completion claim
    [InlineData("{\"failed\": false, \"completed\": false, \"partial\": true}", HermesAgentExecutor.RunIncompleteError)]
    [InlineData("{\"failed\": false, \"completed\": \"true\"}", HermesAgentExecutor.RunIncompleteError)] // not a JSON true
    [InlineData("{\"failed\": true, \"completed\": false}", "Hermes reported a failed run (exit code 0): ")]
    public async Task ExecuteAsync_ExitZero_WithoutACompletedRunReport_IsNeverSuccess_AndNeverRetried(
        string? report, string expectedError)
    {
        var workspace = Directory.CreateTempSubdirectory("hermes-report-").FullName;
        try
        {
            var runner = new FakeProcessRunner(
                _ => new ProcessRunResult(0, "looks done", string.Empty, TimedOut: false),
                writeReport: r => HermesRunReportFake.WriteRaw(r, report));
            var executor = new HermesAgentExecutor(MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

            var result = await executor.ExecuteAsync(new AgentExecutionRequest("coder", null, "do the task", workspace));

            Assert.False(result.Success);
            Assert.Equal(0, result.ExitCode);
            Assert.StartsWith(expectedError, result.Error);
            Assert.False(AgentExecutionFailureClassifier.IsTransientLaunchFailure(result));
            Assert.Empty(Directory.GetFiles(workspace, ".aif-hermes-run-*"));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReportPlacedBeforeTheLaunch_OrOversized_NeverCountsAsSuccess()
    {
        var workspace = Directory.CreateTempSubdirectory("hermes-report-").FullName;
        try
        {
            var runId = Guid.NewGuid();
            var reportPath = Path.Combine(workspace, HermesAgentExecutor.RunReportFileName(runId));
            var executor = (Action<ProcessRunRequest> writeReport) => new HermesAgentExecutor(
                MakeOptions(),
                new FakeProcessRunner(_ => new ProcessRunResult(0, "done", string.Empty, TimedOut: false), writeReport),
                NullLogger<HermesAgentExecutor>.Instance);
            var request = new AgentExecutionRequest("coder", null, "do the task", workspace, AgentRunId: runId);

            // A forged/stale "completed" report left in the agent-writable workspace; this launch writes none.
            File.WriteAllText(reportPath, "{\"failed\": false, \"completed\": true}");
            var stale = await executor(_ => { }).ExecuteAsync(request);

            // A report far beyond Hermes' few hundred bytes is not its report.
            var oversized = await executor(r => HermesRunReportFake.WriteRaw(
                r, "{\"failed\": false, \"completed\": true, \"pad\": \"" + new string('x', 70_000) + "\"}")).ExecuteAsync(request);

            Assert.Equal(HermesAgentExecutor.ReportUnavailableError, stale.Error);
            Assert.Equal(HermesAgentExecutor.ReportUnavailableError, oversized.Error);
            Assert.False(stale.Success || oversized.Success);
            Assert.False(File.Exists(reportPath));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExit_WithASuccessfulRunReport_IsStillFailure()
    {
        var runner = new FakeProcessRunner(_ => new ProcessRunResult(1, "done", "agent crashed", TimedOut: false));
        var executor = new HermesAgentExecutor(MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        var result = await executor.ExecuteAsync(new AgentExecutionRequest("coder", null, "do the task", string.Empty));

        Assert.False(result.Success);
        Assert.Equal("agent crashed", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_OnTimeout()
    {
        var runner = new FakeProcessRunner(_ =>
            new ProcessRunResult(-1, "partial output", string.Empty, TimedOut: true));

        var executor = new HermesAgentExecutor(
            MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            new AgentExecutionRequest("coder", null, "do the task", string.Empty));

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_WhenDockerCannotBeStarted()
    {
        var runner = new FakeProcessRunner(
            new ProcessLaunchException("docker not found", new InvalidOperationException()));

        var executor = new HermesAgentExecutor(
            MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            new AgentExecutionRequest("coder", null, "do the task", string.Empty));

        Assert.False(result.Success);
        Assert.Contains("docker not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_TruncatesOversizedOutput()
    {
        var hugeOutput = new string('x', 1000);

        var runner = new FakeProcessRunner(_ =>
            new ProcessRunResult(0, hugeOutput, string.Empty, TimedOut: false));

        var executor = new HermesAgentExecutor(
            MakeOptions(maxOutputLength: 100), runner, NullLogger<HermesAgentExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            new AgentExecutionRequest("coder", null, "do the task", string.Empty));

        Assert.True(result.Success);
        Assert.NotNull(result.Output);
        Assert.True(result.Output!.Length < hugeOutput.Length);
        Assert.EndsWith("...[truncated]", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation()
    {
        var runner = new FakeProcessRunner(_ =>
            new ProcessRunResult(0, "unused", string.Empty, TimedOut: false));

        var executor = new HermesAgentExecutor(
            MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(
                new AgentExecutionRequest("coder", null, "do the task", string.Empty),
                cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_NeverPassesRawPromptAsShellString_UsesStructuredArguments()
    {
        var runner = new FakeProcessRunner(_ =>
            new ProcessRunResult(0, "ok", string.Empty, TimedOut: false));

        var executor = new HermesAgentExecutor(
            MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        await executor.ExecuteAsync(
            new AgentExecutionRequest("coder", null, "some prompt; rm -rf /", string.Empty));

        Assert.Equal("docker", runner.LastRequest!.FileName);
        Assert.Contains("some prompt; rm -rf /", runner.LastRequest.Arguments);
    }

    // --- Phase 18.14: deterministic agent workspace & execution context ---

    [Fact]
    public async Task ExecuteAsync_NonEmptyWorkingDirectory_IsUsedDirectlyAsBindMountSource_NotAFreshScratchFolder()
    {
        var resolvedWorkspace = Path.Combine(Path.GetTempPath(), "ai-factory-hermes-tests-resolved", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resolvedWorkspace);

        try
        {
            var runner = new FakeProcessRunner(_ =>
                new ProcessRunResult(0, "ok", string.Empty, TimedOut: false));

            var executor = new HermesAgentExecutor(MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

            var result = await executor.ExecuteAsync(
                new AgentExecutionRequest("coder", null, "do the task", resolvedWorkspace));

            Assert.True(result.Success);
            Assert.Equal(resolvedWorkspace, runner.LastRequest!.WorkingDirectory);
            Assert.Contains($"{resolvedWorkspace}:/workspace", runner.LastRequest.Arguments);
            Assert.Equal(resolvedWorkspace, result.WorkspacePath);
        }
        finally
        {
            Directory.Delete(resolvedWorkspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EmptyWorkingDirectory_StillCreatesFreshScratchFolder_UnderWorkspaceRoot()
    {
        var runner = new FakeProcessRunner(_ =>
            new ProcessRunResult(0, "ok", string.Empty, TimedOut: false));

        var options = MakeOptions();
        var executor = new HermesAgentExecutor(options, runner, NullLogger<HermesAgentExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            new AgentExecutionRequest("coder", null, "do the task", string.Empty));

        Assert.True(result.Success);
        Assert.NotNull(runner.LastRequest!.WorkingDirectory);
        Assert.StartsWith(Path.GetFullPath(options.WorkspaceRoot), runner.LastRequest.WorkingDirectory!);
        Assert.True(Directory.Exists(runner.LastRequest.WorkingDirectory));

        Directory.Delete(runner.LastRequest.WorkingDirectory!, recursive: true);
    }

    [Fact]
    public async Task ExecuteAsync_NonexistentWorkingDirectory_FailsDeterministically_WithoutFallingBackToScratch()
    {
        var missingWorkspace = Path.Combine(
            Path.GetTempPath(), "ai-factory-hermes-tests-missing", Guid.NewGuid().ToString("N"));

        var runner = new FakeProcessRunner(_ =>
            new ProcessRunResult(0, "should not run", string.Empty, TimedOut: false));

        var executor = new HermesAgentExecutor(MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            new AgentExecutionRequest("coder", null, "do the task", missingWorkspace));

        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.LastRequest);
        Assert.Null(result.WorkspacePath);
    }

    [Fact]
    public async Task ExecuteAsync_PassesHermesWriteSafeRoot_TargetingWorkspace_ByDefault()
    {
        var runner = new FakeProcessRunner(_ =>
            new ProcessRunResult(0, "ok", string.Empty, TimedOut: false));

        var executor = new HermesAgentExecutor(MakeOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        await executor.ExecuteAsync(new AgentExecutionRequest("coder", null, "do the task", string.Empty));

        Assert.NotNull(runner.LastRequest);
        Assert.Contains("-e", runner.LastRequest!.Arguments);
        Assert.Contains("HERMES_WRITE_SAFE_ROOT=/workspace", runner.LastRequest.Arguments);
    }
}
