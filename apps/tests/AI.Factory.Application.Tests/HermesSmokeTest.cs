using AI.Factory.Application.Abstractions;
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

        var dataVolume = Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_DATA");
        if (string.IsNullOrWhiteSpace(dataVolume))
        {
            dataVolume = Directory.Exists(@"D:\Project\.hermes")
                ? @"D:\Project\.hermes"
                : Path.Combine(Path.GetTempPath(), "ai-factory-hermes-smoke-data");
        }

        var liveBaseUrl = Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_BASE_URL");
        var liveSecretName = Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_API_KEY_SECRET") ?? "OPENROUTER_API_KEY";
        var liveModel = Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_MODEL");

        var options = new HermesOptions
        {
            DockerImage = Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_IMAGE") ?? "nousresearch/hermes-agent:latest",
            DataVolume = dataVolume,
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-hermes-smoke-workspace"),
            TimeoutSeconds = 120,
            MaxOutputLength = 20_000,
            NetworkMode = string.IsNullOrWhiteSpace(liveBaseUrl) ? "none" : "bridge",
            Provider = "openrouter",
            ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
            ProviderApiKeySecretName = liveSecretName,
            DefaultModel = liveModel
        };

        if (!string.IsNullOrWhiteSpace(liveBaseUrl))
        {
            options.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = liveBaseUrl;
        }

        Directory.CreateDirectory(options.DataVolume);
        Directory.CreateDirectory(options.WorkspaceRoot);

        var secretProvider = new AI.Factory.Infrastructure.Configuration.EnvironmentSecretProvider(
            NullLogger<AI.Factory.Infrastructure.Configuration.EnvironmentSecretProvider>.Instance);
        var executor = new HermesAgentExecutor(
            options,
            new SystemProcessRunner(),
            NullLogger<HermesAgentExecutor>.Instance,
            NullAuditLogger.Instance,
            secretProvider);

        var request = new AI.Factory.Application.Abstractions.AgentExecutionRequest(
            "coder",
            liveModel,
            "Create a text file named hello.txt containing exactly: Hello AI Factory",
            string.Empty);

        var result = await executor.ExecuteAsync(request);

        Assert.True(result.Success, $"Hermes execution failed: {result.Error}");
        Assert.False(string.IsNullOrWhiteSpace(result.WorkspacePath));

        var filePath = Path.Combine(result.WorkspacePath!, "hello.txt");
        Assert.True(File.Exists(filePath), $"Expected file not found: {filePath}. Output: {result.Output}. Error: {result.Error}");

        var content = (await File.ReadAllTextAsync(filePath)).Trim();
        Assert.Equal("Hello AI Factory", content);
    }
}
