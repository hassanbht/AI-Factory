namespace AI.Factory.Application.Execution;

/// <summary>
/// Bound from configuration section "Execution" — selects which execution
/// adapter runs an AgentRun: AgentType -> profile -> adapter key. Mirrors the
/// existing ModelRouting AgentTypeCategories idiom (a plain AgentType-keyed map)
/// rather than introducing a new configuration style.
///
/// With no "Execution" section at all, the composition root supplies the one
/// permitted default via <see cref="ApplyBuiltInDefault"/> (a single profile for
/// the default adapter), so existing deployments need no configuration change.
/// Any explicitly configured section must be complete — it is validated at
/// startup and never silently completed.
/// </summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    /// <summary>Profile used for any AgentType not listed in <see cref="AgentTypeProfiles"/>.</summary>
    public string DefaultProfile { get; set; } = string.Empty;

    public Dictionary<string, ExecutionProfileOptions> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>AgentType -> profile name. Unlisted AgentTypes use <see cref="DefaultProfile"/>.</summary>
    public Dictionary<string, string> AgentTypeProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional per-adapter switches, keyed by adapter key. A registered adapter
    /// with no entry is enabled; an entry with Enabled=false makes every profile
    /// that references it fail closed.
    /// </summary>
    public Dictionary<string, ExecutionAdapterOptions> Adapters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Supplies the built-in default ONLY when nothing was configured (no default
    /// profile and no profiles): one profile named after, and pointing at,
    /// <paramref name="defaultAdapterKey"/>. A partially configured section is
    /// left untouched for <see cref="Validate"/> to reject.
    /// </summary>
    public void ApplyBuiltInDefault(string defaultAdapterKey)
    {
        if (string.IsNullOrWhiteSpace(defaultAdapterKey))
        {
            throw new ArgumentException("A default adapter key is required.", nameof(defaultAdapterKey));
        }

        if (!string.IsNullOrWhiteSpace(DefaultProfile) || Profiles.Count > 0)
        {
            return;
        }

        Profiles[defaultAdapterKey] = new ExecutionProfileOptions { Adapter = defaultAdapterKey };
        DefaultProfile = defaultAdapterKey;
    }

    public void Validate()
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(DefaultProfile))
        {
            failures.Add($"{nameof(DefaultProfile)} is required.");
        }
        else if (!Profiles.ContainsKey(DefaultProfile))
        {
            failures.Add($"{nameof(DefaultProfile)} '{DefaultProfile}' is not a configured profile.");
        }

        foreach (var (name, profile) in Profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(profile.Adapter))
            {
                failures.Add($"Profile '{name}' has no {nameof(ExecutionProfileOptions.Adapter)}.");
            }
        }

        foreach (var (agentType, profileName) in AgentTypeProfiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(profileName) || !Profiles.ContainsKey(profileName))
            {
                failures.Add($"AgentType '{agentType}' maps to profile '{profileName}', which is not configured.");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Invalid {nameof(ExecutionOptions)}: {string.Join(" ", failures)}");
        }
    }
}

public sealed class ExecutionProfileOptions
{
    /// <summary>Registry key of the adapter that executes this profile.</summary>
    public string Adapter { get; set; } = string.Empty;

    /// <summary>Optional registry key of the model gateway associated with this profile.</summary>
    public string? Gateway { get; set; }
}

public sealed class ExecutionAdapterOptions
{
    public bool Enabled { get; set; } = true;
}
