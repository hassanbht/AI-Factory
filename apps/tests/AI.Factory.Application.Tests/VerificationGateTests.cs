using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.8 §16 "Unit tests" — deterministic mapping of build/test process
/// outcomes onto VerificationStatus, using fake executors (no real process
/// execution — see VerificationGateRealBuildIntegrationTests for that).
/// </summary>
public sealed class VerificationGateTests
{
    private static readonly ExecutionLimitsOptions Limits = new() { BuildTimeoutMinutes = 1, TestTimeoutMinutes = 1 };

    private sealed class FakeBuildExecutor : IBuildExecutor
    {
        private readonly BuildResult _result;
        public FakeBuildExecutor(BuildResult result) => _result = result;
        public Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed class FakeTestExecutor : ITestExecutor
    {
        private readonly TestExecutionResult _result;
        public FakeTestExecutor(TestExecutionResult result) => _result = result;
        public Task<TestExecutionResult> TestAsync(TestRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed class ThrowingBuildExecutor : IBuildExecutor
    {
        private readonly Exception _exception;
        public ThrowingBuildExecutor(Exception exception) => _exception = exception;
        public Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default) =>
            throw _exception;
    }

    private static (Project project, WorkItem workItem) MakeSubjects()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "P", Slug = "p", RootPath = "x" };
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Title = "T", Description = "d",
            Status = WorkItemStatus.Testing.ToString()
        };
        return (project, workItem);
    }

    [Fact]
    public async Task VerifyAsync_BuildAndTestExitZero_ReturnsPassed()
    {
        var gate = new VerificationGate(
            new FakeBuildExecutor(new BuildResult(true, "ok", "", 0)),
            new FakeTestExecutor(new TestExecutionResult(true, "ok", "", 0)),
            Limits, NullLogger<VerificationGate>.Instance);
        var (project, workItem) = MakeSubjects();

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.Passed, result.Status);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task VerifyAsync_BuildFailsWithNonZeroExit_ReturnsFailed_AndSkipsTests()
    {
        var testExecutor = new FakeTestExecutor(new TestExecutionResult(true, "", "", 0));
        var gate = new VerificationGate(
            new FakeBuildExecutor(new BuildResult(false, "", "error CS1002", 1)),
            testExecutor,
            Limits, NullLogger<VerificationGate>.Instance);
        var (project, workItem) = MakeSubjects();

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.Failed, result.Status);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task VerifyAsync_TestsFailWithNonZeroExit_ReturnsFailed()
    {
        var gate = new VerificationGate(
            new FakeBuildExecutor(new BuildResult(true, "ok", "", 0)),
            new FakeTestExecutor(new TestExecutionResult(false, "1 failed", "", 1)),
            Limits, NullLogger<VerificationGate>.Instance);
        var (project, workItem) = MakeSubjects();

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.Failed, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_BuildTimedOut_ReturnsTimedOut()
    {
        var gate = new VerificationGate(
            new FakeBuildExecutor(new BuildResult(false, "", "", -1, TimedOut: true)),
            new FakeTestExecutor(new TestExecutionResult(true, "", "", 0)),
            Limits, NullLogger<VerificationGate>.Instance);
        var (project, workItem) = MakeSubjects();

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_BuildCouldNotLaunch_ReturnsInfrastructureError()
    {
        // DotNetBuildExecutor's own ProcessLaunchException catch maps to
        // ExitCode=-1, TimedOut=false — the deterministic signal this gate
        // uses to distinguish "could not launch" from a real build failure.
        var gate = new VerificationGate(
            new FakeBuildExecutor(new BuildResult(false, "", "Docker not found", -1, TimedOut: false)),
            new FakeTestExecutor(new TestExecutionResult(true, "", "", 0)),
            Limits, NullLogger<VerificationGate>.Instance);
        var (project, workItem) = MakeSubjects();

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.InfrastructureError, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_UnexpectedException_ReturnsInfrastructureError()
    {
        var gate = new VerificationGate(
            new ThrowingBuildExecutor(new InvalidOperationException("policy violation")),
            new FakeTestExecutor(new TestExecutionResult(true, "", "", 0)),
            Limits, NullLogger<VerificationGate>.Instance);
        var (project, workItem) = MakeSubjects();

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.InfrastructureError, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_ExternalCancellation_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var gate = new VerificationGate(
            new ThrowingBuildExecutor(new OperationCanceledException(cts.Token)),
            new FakeTestExecutor(new TestExecutionResult(true, "", "", 0)),
            Limits, NullLogger<VerificationGate>.Instance);
        var (project, workItem) = MakeSubjects();

        var result = await gate.VerifyAsync(project, workItem, cts.Token);

        Assert.Equal(VerificationStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_FailureSummary_IsBounded()
    {
        var hugeFailureText = string.Join("\n", Enumerable.Repeat("error CS0000: something went wrong", 500));
        var gate = new VerificationGate(
            new FakeBuildExecutor(new BuildResult(false, "", hugeFailureText, 1)),
            new FakeTestExecutor(new TestExecutionResult(true, "", "", 0)),
            Limits, NullLogger<VerificationGate>.Instance);
        var (project, workItem) = MakeSubjects();

        var result = await gate.VerifyAsync(project, workItem);

        Assert.True(result.Summary.Length < hugeFailureText.Length, $"Summary was {result.Summary.Length} chars.");
        Assert.True(result.Summary.Length <= 2200, $"Summary was {result.Summary.Length} chars.");
    }
}
