using AI.Factory.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Real Hermes integration — opt-in only, disabled by default (see Phase 17
/// Section 21). Requires an actual Docker + Hermes image, so it must never
/// run automatically in normal CI. Enable explicitly:
///
///   AI_FACTORY_HERMES_SMOKE_TEST=true dotnet test --filter HermesSmokeTest
///
/// Task: create a file named hello.txt containing "Hello AI Factory" in the
/// agent's workspace, then verify the file exists with matching content.
/// </summary>
public sealed class HermesSmokeTest
{
    [Fact]
    public async Task RealHermesExecution_CreatesRequestedFile_InWorkspace()
    {
        if (!bool.TryParse(Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_SMOKE_TEST"), out var enabled) || !enabled)
        {
            // Disabled by default — never runs automatically in CI. This is
            // a deliberate no-op pass, not a skipped/inconclusive result:
            // xUnit v2 has no first-class "skip at runtime" primitive, so an
            // early return is the standard way to gate an opt-in test.
            return;
        }

        var options = new HermesOptions
        {
            DockerImage = Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_IMAGE") ?? "nousresearch/hermes-agent:latest",
            DataVolume = Path.Combine(Path.GetTempPath(), "ai-factory-hermes-smoke-data"),
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-hermes-smoke-workspace"),
            TimeoutSeconds = 120,
            MaxOutputLength = 20_000,
            NetworkMode = "none"
        };

        Directory.CreateDirectory(options.DataVolume);
        Directory.CreateDirectory(options.WorkspaceRoot);

        var executor = new HermesAgentExecutor(
            options, new SystemProcessRunner(), NullLogger<HermesAgentExecutor>.Instance);

        var request = new AI.Factory.Application.Abstractions.AgentExecutionRequest(
            "coder",
            null,
            "Create a text file named hello.txt containing exactly: Hello AI Factory",
            "/ignored");

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.Success, $"Hermes execution failed: {result.Error}");
        Assert.False(string.IsNullOrWhiteSpace(result.WorkspacePath));

        var filePath = Path.Combine(result.WorkspacePath!, "hello.txt");
        Assert.True(File.Exists(filePath), $"Expected file not found: {filePath}");

        var content = (await File.ReadAllTextAsync(filePath)).Trim();
        Assert.Equal("Hello AI Factory", content);
    }
}
