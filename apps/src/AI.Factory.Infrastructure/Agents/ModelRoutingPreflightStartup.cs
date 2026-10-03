using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// Phase 18.29.1 — one registration and one execution path for the model-routing
/// preflight, shared by the API and the Worker composition roots. Written once
/// rather than twice so the two hosts cannot drift into disagreeing about whether
/// a configuration is releasable.
/// </summary>
public static class ModelRoutingPreflightStartup
{
    /// <param name="httpClientProvider">
    /// Supplied by the composition root because this project does not reference
    /// Microsoft.Extensions.Http — the composition roots own HttpClient creation. Hosts pass
    /// <c>(sp, name) =&gt; sp.GetRequiredService&lt;IHttpClientFactory&gt;().CreateClient(name)</c>.
    /// </param>
    public static void AddModelRoutingPreflight(
        this IServiceCollection services,
        ModelRoutingPreflightOptions preflightOptions,
        AiProviderOptions aiProviderOptions,
        Func<IServiceProvider, string, HttpClient> httpClientProvider)
    {
        services.AddSingleton(preflightOptions);

        // A catalog per ENABLED provider that exposes an OpenAI-compatible /models
        // endpoint. A disabled provider is deliberately skipped: preflighting a
        // provider the deployment will never call would turn an irrelevant outage
        // into deployment noise.
        foreach (var (providerId, settings) in aiProviderOptions.Providers)
        {
            if (!settings.Enabled
                || settings.ProviderType != AiModelProviderKind.OpenAiCompatible
                || string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                continue;
            }

            var capturedId = providerId;
            var capturedBaseUrl = settings.BaseUrl!;

            services.AddSingleton<IProviderModelCatalog>(sp => new OpenAiCompatibleModelCatalog(
                capturedId,
                capturedBaseUrl,
                () => httpClientProvider(sp, $"model-catalog-{capturedId}"),
                sp.GetRequiredService<ILogger<OpenAiCompatibleModelCatalog>>()));
        }

        services.AddSingleton<ModelRoutingPreflight>();
    }

    /// <summary>
    /// Runs the preflight and throws when the configuration names a model the
    /// provider does not serve. Throwing before the host starts is the point: the
    /// process must not reach a state where it accepts work it cannot perform.
    /// A provider outage never throws — see <see cref="ModelRoutingPreflight"/>.
    /// </summary>
    public static async Task RunOrThrowAsync(
        IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<ModelRoutingPreflightOptions>();

        if (!options.Enabled)
        {
            return;
        }

        var preflight = services.GetRequiredService<ModelRoutingPreflight>();
        var report = await preflight.RunAsync(cancellationToken);

        if (report.HasUnservableModel && options.FailStartupOnUnservableModel)
        {
            throw new InvalidOperationException(report.BuildFailureMessage());
        }
    }
}
