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

    /// <summary>
    /// Phase 18.21 — the real verifier, not a fake: none of the WorkItems in
    /// this file declare acceptance criteria, so it deterministically reports
    /// NotSpecified and these tests keep asserting pure build/test semantics.
    /// </summary>
    private static readonly WorkItemAcceptanceVerifier Acceptance =
        new(NullLogger<WorkItemAcceptanceVerifier>.Instance);

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

    /// <summary>
    /// Phase 18.16 §15 — VerificationGate now resolves the workspace via
    /// IWorkspaceResolver before build/test; this fake preserves the exact
    /// prior "use project.RootPath as-is" behavior these outcome-mapping
    /// tests rely on (they are not about workspace resolution semantics —
    /// see WorkspaceResolverTests / VerificationGateWorkspaceResolutionTests
    /// for that).
    /// </summary>
    private sealed class FakeWorkspaceResolver : IWorkspaceResolver
    {
        private readonly WorkspaceResolution? _fixedResult;
        public FakeWorkspaceResolver(WorkspaceResolution? fixedResult = null) => _fixedResult = fixedResult;
        public WorkspaceResolution Resolve(Project project) => _fixedResult ?? WorkspaceResolution.Valid(project.RootPath);
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
            new FakeWorkspaceResolver(), Acceptance, Limits, NullLogger<VerificationGate>.Instance);
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
            new FakeWorkspaceResolver(), Acceptance, Limits, NullLogger<VerificationGate>.Instance);
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
            new FakeWorkspaceResolver(), Acceptance, Limits, NullLogger<VerificationGate>.Instance);
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
            new FakeWorkspaceResolver(), Acceptance, Limits, NullLogger<VerificationGate>.Instance);
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
            new FakeWorkspaceResolver(), Acceptance, Limits, NullLogger<VerificationGate>.Instance);
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
            new FakeWorkspaceResolver(), Acceptance, Limits, NullLogger<VerificationGate>.Instance);
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
            new FakeWorkspaceResolver(), Acceptance, Limits, NullLogger<VerificationGate>.Instance);
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
            new FakeWorkspaceResolver(), Acceptance, Limits, NullLogger<VerificationGate>.Instance);
        var (project, workItem) = MakeSubjects();

        var result = await gate.VerifyAsync(project, workItem);

        Assert.True(result.Summary.Length < hugeFailureText.Length, $"Summary was {result.Summary.Length} chars.");
        Assert.True(result.Summary.Length <= 2200, $"Summary was {result.Summary.Length} chars.");
    }

    /// <summary>Records the RootPath/ProjectPath each executor was actually invoked with, to prove it is the resolver's output.</summary>
    private sealed class RecordingBuildExecutor : IBuildExecutor
    {
        public string? ReceivedPath { get; private set; }
        public Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default)
        {
            ReceivedPath = request.ProjectPath;
            return Task.FromResult(new BuildResult(true, "ok", "", 0));
        }
    }

    private sealed class RecordingTestExecutor : ITestExecutor
    {
        public string? ReceivedPath { get; private set; }
        public Task<TestExecutionResult> TestAsync(TestRequest request, CancellationToken cancellationToken = default)
        {
            ReceivedPath = request.ProjectPath;
            return Task.FromResult(new TestExecutionResult(true, "ok", "", 0));
        }
    }

    /// <summary>
    /// Phase 18.16 §15 — the single most important invariant: the RESOLVED
    /// workspace path (not the raw, un-resolved project.RootPath) is what
    /// reaches both BuildExecutor and TestExecutor.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_UsesResolvedWorkspacePath_NotRawRootPath_ForBuildAndTest()
    {
        var (project, workItem) = MakeSubjects();
        const string resolvedPath = "/resolved/workspace/path";

        var build = new RecordingBuildExecutor();
        var test = new RecordingTestExecutor();
        var gate = new VerificationGate(
            build, test, new FakeWorkspaceResolver(WorkspaceResolution.Valid(resolvedPath)),
            Acceptance, Limits, NullLogger<VerificationGate>.Instance);

        await gate.VerifyAsync(project, workItem);

        Assert.Equal(resolvedPath, build.ReceivedPath);
        Assert.Equal(resolvedPath, test.ReceivedPath);
        Assert.NotEqual(project.RootPath, build.ReceivedPath);
    }

    [Fact]
    public async Task VerifyAsync_WorkspaceResolutionFails_ReturnsInfrastructureError_NoSilentCwdFallback()
    {
        var (project, workItem) = MakeSubjects();
        var gate = new VerificationGate(
            new FakeBuildExecutor(new BuildResult(true, "", "", 0)),
            new FakeTestExecutor(new TestExecutionResult(true, "", "", 0)),
            new FakeWorkspaceResolver(WorkspaceResolution.Invalid("simulated resolution failure")),
            Acceptance, Limits, NullLogger<VerificationGate>.Instance);

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.InfrastructureError, result.Status);
        Assert.Contains("simulated resolution failure", result.Summary);
    }

    [Fact]
    public async Task VerifyProjectAsync_WorkspaceResolutionFails_ReturnsInfrastructureError()
    {
        var (project, _) = MakeSubjects();
        var gate = new VerificationGate(
            new FakeBuildExecutor(new BuildResult(true, "", "", 0)),
            new FakeTestExecutor(new TestExecutionResult(true, "", "", 0)),
            new FakeWorkspaceResolver(WorkspaceResolution.Invalid("simulated resolution failure")),
            Acceptance, Limits, NullLogger<VerificationGate>.Instance);

        var result = await gate.VerifyProjectAsync(project);

        Assert.Equal(VerificationStatus.InfrastructureError, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_TestsFailWithFailureMarker_TailPreservesFailureSectionOverBanner()
    {
        var (project, workItem) = MakeSubjects();
        var bannerNoise = new string('x', 600);
        var failureDetails = "[FAIL] TestName Assert.Equal() Failure: Expected: A, Actual: B";
        var testOutput = $"{bannerNoise}\n{failureDetails}";

        var gate = new VerificationGate(
            new FakeBuildExecutor(new BuildResult(true, "", "", 0)),
            new FakeTestExecutor(new TestExecutionResult(false, testOutput, "", 1)),
            new FakeWorkspaceResolver(),
            Acceptance, Limits, NullLogger<VerificationGate>.Instance);

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.Failed, result.Status);
        Assert.Contains(failureDetails, result.Summary);
    }

    // ---------------- Phase M — workspace integrity is part of verification ----------------

    private sealed class FixedIntegrityInspector : IWorkspaceIntegrityInspector
    {
        private readonly IReadOnlyList<WorkspaceFileChange>? _changes;
        public FixedIntegrityInspector(IReadOnlyList<WorkspaceFileChange>? changes) => _changes = changes;
        public Task<IReadOnlyList<WorkspaceFileChange>?> GetChangesAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_changes);
    }

    private sealed class CountingBuildExecutor : IBuildExecutor
    {
        public int Calls { get; private set; }

        public Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new BuildResult(true, "ok", "", 0));
        }
    }

    [Fact]
    public async Task VerifyAsync_IntegrityViolation_IsARepairableFailure_NamingTheViolation_BeforeAnyBuild()
    {
        var build = new CountingBuildExecutor();
        var gate = new VerificationGate(
            build, new FakeTestExecutor(new TestExecutionResult(true, "ok", "", 0)), new FakeWorkspaceResolver(), Acceptance, Limits,
            NullLogger<VerificationGate>.Instance,
            new FixedIntegrityInspector(new[] { new WorkspaceFileChange("dotnet-install.sh", WorkspaceChangeKind.Added, null, "#!/bin/sh") }));
        var (project, workItem) = MakeSubjects();

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.Failed, result.Status); // Failed -> Repairing, never Passed
        Assert.Contains("dotnet-install.sh", result.Summary);
        Assert.Equal(0, build.Calls);
    }

    [Theory]
    [InlineData(true)]   // clean change set
    [InlineData(false)]  // no git baseline: integrity unavailable, build/test semantics unchanged
    public async Task VerifyAsync_CleanOrUnavailableIntegrity_KeepsBuildAndTestSemantics(bool hasBaseline)
    {
        var gate = new VerificationGate(
            new FakeBuildExecutor(new BuildResult(true, "ok", "", 0)), new FakeTestExecutor(new TestExecutionResult(true, "ok", "", 0)),
            new FakeWorkspaceResolver(), Acceptance, Limits, NullLogger<VerificationGate>.Instance,
            new FixedIntegrityInspector(hasBaseline ? new[] { new WorkspaceFileChange("src/A.cs", WorkspaceChangeKind.Modified) } : null));
        var (project, workItem) = MakeSubjects();

        Assert.Equal(VerificationStatus.Passed, (await gate.VerifyAsync(project, workItem)).Status);
    }
}
