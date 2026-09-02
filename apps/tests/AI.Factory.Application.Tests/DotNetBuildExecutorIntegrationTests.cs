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
