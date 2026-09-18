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

        public FakeProcessRunner(Func<ProcessRunRequest, ProcessRunResult> respond)
        {
            _respond = respond;
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
            LastRequest = request;

            cancellationToken.ThrowIfCancellationRequested();

            if (_throwOnRun is not null)
            {
                throw _throwOnRun;
            }

            return Task.FromResult(_respond(request));
        }
    }

    private static HermesOptions MakeOptions(int maxOutputLength = 100_000)
    {
        return new HermesOptions
        {
            DockerImage = "test/image:latest",
            DataVolume = "/host/data",
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
