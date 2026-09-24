using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Phase 18.1.7 §11 — the dedicated API-to-Hermes E2E harness. ApiTestFactory
/// never registers AgentRunWorker (only apps/src/AI.Factory.Worker does), so
/// this test proves the same HTTP-visible path a real Worker would produce by
/// resolving the real (unfaked) IAgentRunRepository/IAgentOrchestrator already
/// registered in Api's own composition root and performing the exact
/// claim -> orchestrate sequence AgentRunWorker.PollOnceAsync performs, in
/// between the POST and the GET. Opt-in only, disabled by default:
///
///   AI_FACTORY_HERMES_SMOKE_TEST=true dotnet test --filter RealHermesApiE2ETests
/// </summary>
public sealed class RealHermesApiE2ETests
{
    private static bool Enabled =>
        bool.TryParse(Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_SMOKE_TEST"), out var enabled) && enabled;

    // Phase D — the same AI_FACTORY_LIVE_* variables the Worker live harness reads
    // (LiveWorkerHost), with the previously hard-coded OmniRoute values as defaults.
    private static string LiveBaseUrl =>
        Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_BASE_URL") ?? "http://host.docker.internal:20128/v1";

    private static string LiveModel =>
        Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_MODEL") ?? "auto/coding:free";

    private static string LiveApiKeySecretName =>
        Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_API_KEY_SECRET") ?? "OMNIROUTE_API_KEY";

    [Fact]
    public async Task PostRun_ThenRealClaimAndOrchestrate_ThenGet_ShowsSucceeded()
    {
        if (!Enabled)
        {
            return;
        }

        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-api-e2e-workspace-" + Guid.NewGuid().ToString("N"));
        var dataVolume = Path.Combine(Path.GetTempPath(), "ai-factory-api-e2e-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(dataVolume);

        using var baseFactory = new ApiTestFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(HermesOptions));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            var hermesOptions = new HermesOptions
            {
                DockerImage = Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_IMAGE") ?? "nousresearch/hermes-agent:latest",
                DataVolume = dataVolume,
                WorkspaceRoot = workspaceRoot,
                TimeoutSeconds = 120,
                MaxOutputLength = 20_000,
                // Routed through a local OmniRoute gateway (github.com/diegosouzapw/OmniRoute) —
                // OpenRouter directly has no billing/credits for Hermes's default model in this
                // environment. See .ai/context/phase-18.1.7-agent-e2e-stability.md.
                NetworkMode = "bridge",
                Provider = "openrouter",
                ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
                ProviderApiKeySecretName = LiveApiKeySecretName
            };
            hermesOptions.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = LiveBaseUrl;
            services.AddSingleton(hermesOptions);
        }));

        try
        {
            var client = factory.CreateClient();

            var definitionResponse = await client.PostAsJsonAsync("/api/agents/definitions", new
            {
                Name = $"Coder {Guid.NewGuid():N}",
                AgentType = "Coding"
            });
            var definitionBody = await definitionResponse.Content.ReadFromJsonAsync<JsonElement>();
            var definitionId = definitionBody.GetProperty("id").GetGuid();

            var runResponse = await client.PostAsJsonAsync("/api/agents/runs", new
            {
                AgentDefinitionId = definitionId,
                Input = "Respond with exactly this text and nothing else: AI_FACTORY_HERMES_SMOKE_OK"
            });
            Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
            var runBody = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
            var runId = runBody.GetProperty("id").GetGuid();
            Assert.Equal("Queued", runBody.GetProperty("status").GetString());

            // The exact sequence AgentRunWorker.PollOnceAsync performs — claim,
            // then orchestrate — against Api's own real (unfaked) DI graph.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var runRepository = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();

                // CreateAgentRunRequest has no Model field yet (nothing in the
                // production pipeline populates AgentRun.Model) — set it
                // directly so HermesAgentExecutor's -m flag selects a real,
                // reachable model via OmniRoute.
                var created = await runRepository.GetByIdAsync(runId);
                created!.Model = LiveModel;
                await runRepository.UpdateAsync(created);

                var claimed = await runRepository.TryClaimNextPendingAsync();
                Assert.NotNull(claimed);
                Assert.Equal(runId, claimed!.Id);

                var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
                var result = await orchestrator.ExecuteAsync(claimed.Id);
                Assert.True(result.Success, result.ErrorMessage);
            }

            var getResponse = await client.GetAsync($"/api/agents/runs/{runId}");
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Succeeded", getBody.GetProperty("status").GetString());
            Assert.Contains("AI_FACTORY_HERMES_SMOKE_OK", getBody.GetProperty("output").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var path in new[] { workspaceRoot, dataVolume })
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
