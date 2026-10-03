using AI.Factory.Worker.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;
using AI.Factory.TestSupport;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase J (J17/J19) / Phase K (K11-K13) — one real, disposable AgentRun per runtime through the Production Worker
/// composition: scheduler -> AgentRunWorker -> AgentOrchestrator -> AdapterDispatchingAgentRuntime
/// -> Hermes/OpenHands execution adapter -> ModelRoute -> ModelGatewayResolver -> real container
/// -> real OpenRouter with the explicit model. Throwaway SQLite database, throwaway workspace and
/// bare repository (BenchmarkHarness).
///
/// Opt-in (real containers, minutes per run): set AI_FACTORY_LIVE_GATEWAY_AGENTRUN=true and
/// OPENROUTER_API_KEY; otherwise SKIPPED / ENVIRONMENT BLOCKED, never passed. The assertion is the Phase J truthfulness invariant, whichever way the
/// external gateway answers: a Succeeded AgentRun must carry measured model usage, and a run
/// that did not succeed must never reach WorkItem Completed.
/// </summary>
[Collection(TestCollections.DockerAndBuilds)]
public sealed class GatewayLiveAgentRunTests
{
    private readonly ITestOutputHelper _output;

    public GatewayLiveAgentRunTests(ITestOutputHelper output) => _output = output;

    [LiveTheory("AI_FACTORY_LIVE_GATEWAY_AGENTRUN", RequiresDocker = true, RequiredEnvironment = new[] { "OPENROUTER_API_KEY" })]
    [InlineData("Hermes", null)]
    [InlineData("OpenHands", null)]
    public Task LiveDisposableAgentRun_ReportsTheGatewayOutcomeTruthfully(string adapter, string? gateway) =>
        RunLiveAgentRunAsync(adapter, gateway, omniRouteUrl: null);

    // Phase K — the OmniRoute case routes the run explicitly to the OmniRoute gateway, reached from
    // inside the OpenHands container at AI_FACTORY_LIVE_OMNIROUTE_URL (e.g. host.docker.internal).
    [LiveFact("AI_FACTORY_LIVE_GATEWAY_AGENTRUN", RequiresDocker = true,
        RequiredEnvironment = new[] { "OPENROUTER_API_KEY", "AI_FACTORY_LIVE_OMNIROUTE_URL" })]
    public Task LiveDisposableAgentRun_OmniRouteGateway_ReportsTheGatewayOutcomeTruthfully() =>
        RunLiveAgentRunAsync("OpenHands", "OmniRoute", Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_OMNIROUTE_URL"));

    private async Task RunLiveAgentRunAsync(string adapter, string? gateway, string? omniRouteUrl)
    {
        var model = gateway is null
            ? BenchmarkHarness.ControlledModel
            : Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_OMNIROUTE_MODEL") ?? "openai/qwen2.5-coder:7b";

        var root = Path.Combine(Path.GetTempPath(), $"aif-live-gw-{adapter.ToLowerInvariant()}-{Guid.NewGuid():N}"[..28]);
        Directory.CreateDirectory(root);

        try
        {
            var (bareRepo, commitSha, baselineHash) = BenchmarkHarness.CreateCanonicalBaselineBareRepo(root);

            var record = await BenchmarkHarness.ExecuteIterationAsync(
                adapter, 1, bareRepo, commitSha, baselineHash, model, output: _output,
                gateway: gateway, gatewayBaseUrl: gateway is null ? null : omniRouteUrl);

            _output.WriteLine($"[LIVE AGENTRUN {adapter}] RunId={record.RunId} Model={record.Model} Gateway={gateway ?? "OpenRouter (default)"}");
            _output.WriteLine($"[LIVE AGENTRUN {adapter}] AgentRun={record.TerminalStatus} FailureCategory={record.FailureCategory} "
                + $"ExitReason={record.ExitReason} GatewayFailure={record.GatewayFailure ?? "NOT AVAILABLE"}");
            _output.WriteLine($"[LIVE AGENTRUN {adapter}] Duration={record.ExecutionDurationSeconds:F1}s Tokens(in/out/total)="
                + $"{Show(record.InputTokens)}/{Show(record.OutputTokens)}/{Show(record.TotalTokens)}");
            _output.WriteLine($"[LIVE AGENTRUN {adapter}] WorkItem={record.WorkItemStatus} ChangedFiles={record.ChangedFiles.Count} "
                + $"Artifacts={record.ArtifactCount} ContainersCleanedUp={record.CleanupVerified}");
            _output.WriteLine($"[LIVE AGENTRUN {adapter}] ChangedFileList={string.Join(", ", record.ChangedFiles.Select(f => $"{f}:{record.FileClassifications.GetValueOrDefault(f)}"))}");
            _output.WriteLine($"[LIVE AGENTRUN {adapter}] Error={record.ErrorMessage}");
            _output.WriteLine($"[LIVE AGENTRUN {adapter}] HarnessIndependentVerification={record.AcceptanceStatus} (harness step, not Factory lifecycle)");

            if (record.TerminalStatus == "Succeeded")
            {
                Assert.True(record.TotalTokens is > 0 || record.InputTokens is > 0,
                    "A Succeeded AgentRun reported no measured model usage — success without evidence of generation.");
            }
            else
            {
                Assert.NotEqual("Completed", record.WorkItemStatus);
                Assert.Equal(0, record.ArtifactCount);
            }

            Assert.DoesNotContain(record.ChangedFiles, f => f.Contains(".aif-hermes-run-", StringComparison.Ordinal));
            // Phase L — the OpenHands Agent Server's own state never shows up in the Project's git status.
            Assert.DoesNotContain(record.ChangedFiles, f =>
                f.StartsWith("conversations", StringComparison.Ordinal) || f.StartsWith("bash_events", StringComparison.Ordinal));
            Assert.True(record.CleanupVerified, "Factory-owned containers were left running.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string Show(long? value) => value?.ToString() ?? "NOT MEASURED";
}
