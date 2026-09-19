using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

public sealed class ModelRoutingPreflightOptions
{
    public const string SectionName = "ModelRoutingPreflight";

    /// <summary>
    /// Off by default so dev/test hosts keep working with no network and no
    /// provider catalog registered. Production turns it on explicitly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true (production), a configured model the provider does not serve
    /// aborts startup. A provider OUTAGE never aborts startup regardless of this
    /// flag — see <see cref="ModelRoutingPreflight"/>.
    /// </summary>
    public bool FailStartupOnUnservableModel { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>Model id configured for a provider that demonstrably does not serve it.</summary>
public sealed record UnservableModel(string ModelId, string Provider, IReadOnlyList<string> Categories);

/// <summary>Provider that could not be asked. Distinct from, and never reported as, an invalid model.</summary>
public sealed record UnreachableProvider(string Provider, string Reason);

public sealed record VerifiedModel(string ModelId, string Provider);

public sealed record ModelRoutingPreflightReport(
    bool Executed,
    IReadOnlyList<VerifiedModel> Verified,
    IReadOnlyList<UnservableModel> Unservable,
    IReadOnlyList<UnreachableProvider> UnreachableProviders,
    IReadOnlyList<string> ProvidersWithoutCatalog)
{
    public static ModelRoutingPreflightReport Skipped() => new(
        false, Array.Empty<VerifiedModel>(), Array.Empty<UnservableModel>(),
        Array.Empty<UnreachableProvider>(), Array.Empty<string>());

    public bool HasUnservableModel => Unservable.Count > 0;

    /// <summary>
    /// Deliberately names the model, the provider and the affected roles, and
    /// says what is NOT being done about it — the one failure mode this whole
    /// mechanism exists to prevent is a release quietly substituting a working
    /// model for a broken one and shipping a configuration nobody verified.
    /// </summary>
    public string BuildFailureMessage()
    {
        var lines = Unservable.Select(u =>
            $"  - Configured model '{u.ModelId}' is not available from provider '{u.Provider}'"
            + $" (affects: {string.Join(", ", u.Categories)}).");

        return "Model routing preflight rejected this configuration. "
            + "Production startup is aborted rather than substituting a model:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Phase 18.29.1 — checks every configured model/provider pair against what the
/// provider actually serves, before the host accepts work.
///
/// What it must never do, and does not do: pick a different model, fall back to an
/// auto-router, or drop the offending entry. An invalid model id is a configuration
/// defect and the only correct response is to refuse the configuration loudly. A
/// provider outage is a different condition — it proves nothing about any model id,
/// so it is reported and logged but never fails startup, or every provider blip
/// would become an unbootable deployment.
/// </summary>
public sealed class ModelRoutingPreflight
{
    private readonly ModelRoutingOptions _routingOptions;
    private readonly ModelRoutingPreflightOptions _preflightOptions;
    private readonly IReadOnlyList<IProviderModelCatalog> _catalogs;
    private readonly ILogger<ModelRoutingPreflight> _logger;

    public ModelRoutingPreflight(
        ModelRoutingOptions routingOptions,
        ModelRoutingPreflightOptions preflightOptions,
        IEnumerable<IProviderModelCatalog> catalogs,
        ILogger<ModelRoutingPreflight> logger)
    {
        _routingOptions = routingOptions;
        _preflightOptions = preflightOptions;
        _catalogs = catalogs.ToList();
        _logger = logger;
    }

    public async Task<ModelRoutingPreflightReport> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_preflightOptions.Enabled)
        {
            _logger.LogInformation("ModelRoutingPreflightSkipped Reason=Disabled");
            return ModelRoutingPreflightReport.Skipped();
        }

        var verified = new List<VerifiedModel>();
        var unservable = new List<UnservableModel>();
        var unreachable = new List<UnreachableProvider>();
        var withoutCatalog = new List<string>();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(_preflightOptions.TimeoutSeconds));

        // One lookup per provider, not per model — the catalog is per provider and
        // a preflight that fanned out per model would turn a six-role configuration
        // into six identical provider calls at every startup.
        foreach (var providerGroup in _routingOptions.Models
            .Where(m => !string.IsNullOrWhiteSpace(m.Provider) && !string.IsNullOrWhiteSpace(m.ModelId))
            .GroupBy(m => m.Provider!, StringComparer.OrdinalIgnoreCase))
        {
            var provider = providerGroup.Key;
            var catalog = _catalogs.FirstOrDefault(c =>
                string.Equals(c.Provider, provider, StringComparison.OrdinalIgnoreCase));

            if (catalog is null)
            {
                // Not a failure: a provider with no catalog implementation simply
                // cannot be preflighted. Reported so it is visible rather than
                // looking like a silent pass.
                withoutCatalog.Add(provider);
                _logger.LogWarning(
                    "ModelRoutingPreflightNoCatalog Provider={Provider} — configured models for this provider were not verified.",
                    provider);
                continue;
            }

            ProviderModelCatalogResult result;

            try
            {
                result = await catalog.GetAvailableModelIdsAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = ProviderModelCatalogResult.Unavailable(
                    $"Catalog lookup exceeded {_preflightOptions.TimeoutSeconds}s.");
            }

            if (result.Status == ProviderModelCatalogStatus.Unavailable)
            {
                unreachable.Add(new UnreachableProvider(provider, result.UnavailableReason ?? "unknown"));

                _logger.LogWarning(
                    "ModelRoutingPreflightProviderUnavailable Provider={Provider} Reason={Reason} — "
                        + "this is a provider outage, NOT an invalid model id; configured models were not verified.",
                    provider, result.UnavailableReason);
                continue;
            }

            var available = new HashSet<string>(result.ModelIds, StringComparer.OrdinalIgnoreCase);

            foreach (var model in providerGroup)
            {
                if (available.Contains(model.ModelId))
                {
                    verified.Add(new VerifiedModel(model.ModelId, provider));

                    _logger.LogInformation(
                        "ModelRoutingPreflightVerified Provider={Provider} Model={ModelId}", provider, model.ModelId);
                    continue;
                }

                var categories = _routingOptions.CategoryProfiles
                    .Where(kvp => kvp.Value.Contains(model.ModelId, StringComparer.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key)
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList();

                unservable.Add(new UnservableModel(model.ModelId, provider, categories));

                _logger.LogError(
                    "ModelRoutingPreflightUnservableModel Provider={Provider} Model={ModelId} Categories={Categories} — "
                        + "configured model is not available from this provider.",
                    provider, model.ModelId, string.Join(",", categories));
            }
        }

        return new ModelRoutingPreflightReport(true, verified, unservable, unreachable, withoutCatalog);
    }
}
