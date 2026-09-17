using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Validation;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Isolated integration test: exercises the real `dotnet build` command
/// through DotNetBuildExecutor. Targets this repo's own already-restored
/// AI.Factory.Domain.csproj (small, no network needed) rather than
/// scaffolding a throwaway project — fast and deterministic.
/// </summary>
public sealed class DotNetBuildExecutorIntegrationTests
{
    private static string DomainProjectPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/AI.Factory.Domain/AI.Factory.Domain.csproj"));

    /// <summary>
    /// Phase 18.23 — the verification gate hung until its 5-minute timeout on a
    /// machine whose MSBuild daemons were not already running: the daemons
    /// `dotnet build` leaves behind inherit the runner's redirected stdout, so
    /// the pipe never reaches EOF even though the build succeeded. These flags
    /// are the fix, and nothing else in the pipeline would catch their removal —
    /// hence a test on the arguments themselves.
    /// </summary>
    [Fact]
    public async Task BuildAsync_DisablesTheMsBuildDaemons_SoTheRedirectedPipeCanClose()
    {
        var runner = new CapturingProcessRunner();

        await new DotNetBuildExecutor(runner).BuildAsync(
            new BuildRequest("some/project", TimeSpan.FromMinutes(1)));

        Assert.Equal("dotnet", runner.FileName);
        Assert.Contains("-nodeReuse:false", runner.Arguments);
        Assert.Contains("-p:UseSharedCompilation=false", runner.Arguments);
    }

    /// <summary>`dotnet test` drives the same MSBuild machinery, so it carries the same hazard.</summary>
    [Fact]
    public async Task TestAsync_DisablesTheMsBuildDaemons_Too()
    {
        var runner = new CapturingProcessRunner();

        await new DotNetTestExecutor(runner).TestAsync(
            new TestRequest("some/project", TimeSpan.FromMinutes(1)));

        Assert.Equal("dotnet", runner.FileName);
        Assert.Contains("-nodeReuse:false", runner.Arguments);
        Assert.Contains("-p:UseSharedCompilation=false", runner.Arguments);
    }

    private sealed class CapturingProcessRunner : IProcessRunner
    {
        public string? FileName { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            FileName = request.FileName;
            Arguments = request.Arguments.ToList();
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty, false));
        }
    }

    [Fact]
    public async Task BuildAsync_ReturnsSuccess_ForRealProject()
    {
        var executor = new DotNetBuildExecutor(new SystemProcessRunner());

        var result = await executor.BuildAsync(
            new BuildRequest(DomainProjectPath, TimeSpan.FromMinutes(2)));

        Assert.True(result.Success, result.StandardOutput + result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task BuildAsync_ReturnsFailure_ForMissingProjectPath()
    {
        var executor = new DotNetBuildExecutor(new SystemProcessRunner());

        var result = await executor.BuildAsync(
            new BuildRequest(
                Path.Combine(Path.GetTempPath(), "definitely-does-not-exist.csproj"),
                TimeSpan.FromSeconds(30)));

        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
    }
}
