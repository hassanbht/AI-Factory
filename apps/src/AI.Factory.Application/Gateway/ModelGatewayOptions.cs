namespace AI.Factory.Application.Gateway;

/// <summary>
/// Strongly typed configuration options for AI Model Gateways.
/// Bound from configuration section "ModelGateway".
/// </summary>
public sealed class ModelGatewayOptions
{
    public const string SectionName = "ModelGateway";

    /// <summary>Default gateway key when none is specified in ModelRoute or ExecutionProfile (e.g. "OpenRouter").</summary>
    public string DefaultGateway { get; set; } = "OpenRouter";

    /// <summary>Per-gateway configuration options, keyed by gateway key (case-insensitive).</summary>
    public Dictionary<string, GatewayEntryOptions> Gateways { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="execution">
    /// Phase J — when given, every execution profile that names a gateway must name a configured,
    /// enabled one, so a misrouted profile fails startup instead of its first AgentRun.
    /// </param>
    /// <param name="catalogProviders">
    /// Phase K — the AiProviders entries the model-routing preflight reads its catalogs from. An
    /// enabled entry that names an enabled gateway must point at the same endpoint, so the
    /// preflight never vouches for a different gateway than the runtimes are sent to.
    /// </param>
    public void Validate(
        Execution.ExecutionOptions? execution = null,
        Configuration.AiProviderOptions? catalogProviders = null)
    {
        if (string.IsNullOrWhiteSpace(DefaultGateway))
        {
            throw new InvalidOperationException($"{nameof(DefaultGateway)} is required.");
        }

        foreach (var (key, entry) in Gateways)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Gateway key cannot be empty.");
            }

            if (entry.Enabled && string.IsNullOrWhiteSpace(entry.BaseUrl))
            {
                throw new InvalidOperationException($"Enabled gateway '{key}' must have a BaseUrl configured.");
            }
        }

        // Phase K — unrouted runs (the Planner's direct Hermes calls) go to the default gateway.
        if (Gateways.Count > 0
            && (!Gateways.TryGetValue(DefaultGateway, out var defaultGateway) || !defaultGateway.Enabled))
        {
            throw new InvalidOperationException(
                $"{nameof(DefaultGateway)} '{DefaultGateway}' must name a configured, enabled model gateway.");
        }

        foreach (var (providerId, provider) in catalogProviders?.Providers ?? new())
        {
            if (provider.Enabled
                && Gateways.TryGetValue(providerId, out var gateway)
                && gateway.Enabled
                && !string.Equals(provider.BaseUrl?.TrimEnd('/'), gateway.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"AiProviders entry '{providerId}' (model catalog) points at '{provider.BaseUrl}', but model gateway "
                        + $"'{providerId}' points at '{gateway.BaseUrl}'. One endpoint per gateway.");
            }
        }

        if (execution is null || Gateways.Count == 0)
        {
            return;
        }

        foreach (var (profileName, profile) in execution.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Gateway))
            {
                continue;
            }

            if (!Gateways.TryGetValue(profile.Gateway, out var gateway))
            {
                throw new InvalidOperationException(
                    $"Execution profile '{profileName}' routes to model gateway '{profile.Gateway}', which is not configured.");
            }

            if (!gateway.Enabled)
            {
                throw new InvalidOperationException(
                    $"Execution profile '{profileName}' routes to model gateway '{profile.Gateway}', which is disabled.");
            }
        }
    }
}

/// <summary>
/// One gateway's transport configuration. No timeout: the runtimes make the model requests, and
/// the AgentRun deadline is the single execution timeout owner.
/// </summary>
public sealed class GatewayEntryOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKeySecretName { get; set; }
}
