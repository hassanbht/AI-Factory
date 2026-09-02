using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ValidationEngineTests
{
    private sealed class FakeBuildExecutor : IBuildExecutor
    {
        private readonly BuildResult _result;

        public FakeBuildExecutor(BuildResult result)
        {
            _result = result;
        }

        public Task<BuildResult> BuildAsync(
            BuildRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeTestExecutor : ITestExecutor
    {
        private readonly TestExecutionResult _result;

        public FakeTestExecutor(TestExecutionResult result)
        {
            _result = result;
        }

        public bool WasCalled { get; private set; }

        public Task<TestExecutionResult> TestAsync(
            TestRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task ValidateAsync_SkipsTests_WhenBuildFails()
    {
        var buildExecutor = new FakeBuildExecutor(
            new BuildResult(false, "restoring...", "error CS1002: ; expected", 1));
        var testExecutor = new FakeTestExecutor(
            new TestExecutionResult(true, string.Empty, string.Empty, 0));

        var engine = new ValidationEngine(buildExecutor, testExecutor, new ExecutionLimitsOptions());

        var result = await engine.ValidateAsync(new ValidationRequest(Guid.NewGuid(), "/some/path"));

        Assert.False(result.Success);
        Assert.False(result.BuildSucceeded);
        Assert.Null(result.TestsSucceeded);
        Assert.False(testExecutor.WasCalled);
        Assert.Contains(result.Failures, f => f.Category == "Build" && f.Message.Contains("CS1002"));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsSuccess_WhenBuildAndTestsSucceed()
    {
        var buildExecutor = new FakeBuildExecutor(
            new BuildResult(true, "Build succeeded.", string.Empty, 0));
        var testExecutor = new FakeTestExecutor(
            new TestExecutionResult(true, "Passed! - Passed: 5", string.Empty, 0));

        var engine = new ValidationEngine(buildExecutor, testExecutor, new ExecutionLimitsOptions());

        var result = await engine.ValidateAsync(new ValidationRequest(Guid.NewGuid(), "/some/path"));

        Assert.True(result.Success);
        Assert.True(result.BuildSucceeded);
        Assert.True(result.TestsSucceeded);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsFailure_WhenTestsFail_AfterBuildSucceeds()
    {
        var buildExecutor = new FakeBuildExecutor(
            new BuildResult(true, "Build succeeded.", string.Empty, 0));
        var testExecutor = new FakeTestExecutor(
            new TestExecutionResult(false, "Failed! - Failed: 1, Passed: 4", string.Empty, 1));

        var engine = new ValidationEngine(buildExecutor, testExecutor, new ExecutionLimitsOptions());

        var result = await engine.ValidateAsync(new ValidationRequest(Guid.NewGuid(), "/some/path"));

        Assert.False(result.Success);
        Assert.True(result.BuildSucceeded);
        Assert.False(result.TestsSucceeded);
        Assert.Contains(result.Failures, f => f.Category == "Test" && f.Message.Contains("Failed"));
    }

    [Fact]
    public async Task ValidateAsync_NormalizesFailures_FilteringNoiseLines()
    {
        var buildExecutor = new FakeBuildExecutor(new BuildResult(
            false,
            "Restore complete.\nBuilding project...",
            "error CS0246: The type or namespace name 'Foo' could not be found",
            1));
        var testExecutor = new FakeTestExecutor(
            new TestExecutionResult(true, string.Empty, string.Empty, 0));

        var engine = new ValidationEngine(buildExecutor, testExecutor, new ExecutionLimitsOptions());

        var result = await engine.ValidateAsync(new ValidationRequest(Guid.NewGuid(), "/some/path"));

        Assert.Single(result.Failures);
        Assert.Contains("CS0246", result.Failures[0].Message);
    }
}
