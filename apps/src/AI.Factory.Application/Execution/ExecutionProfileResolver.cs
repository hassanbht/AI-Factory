using AI.Factory.Application.Abstractions.Execution;

namespace AI.Factory.Application.Execution;

/// <summary>
/// AgentType -> execution profile -> execution adapter. Fails closed: an
/// unconfigured profile, a profile without an adapter, a disabled adapter or an
/// adapter key nothing registered all produce an unresolved result — never a
/// substitute adapter. The only default is <see cref="ExecutionOptions.DefaultProfile"/>
/// for AgentTypes that have no explicit mapping.
/// </summary>
public sealed class ExecutionProfileResolver
{
    private readonly ExecutionOptions _options;
    private readonly IExecutionAdapterRegistry _registry;

    public ExecutionProfileResolver(ExecutionOptions options, IExecutionAdapterRegistry registry)
    {
        _options = options;
        _registry = registry;
    }

    public ExecutionProfileResolution Resolve(string? agentType)
    {
        var profileName = !string.IsNullOrWhiteSpace(agentType)
            && _options.AgentTypeProfiles.TryGetValue(agentType, out var mapped)
                ? mapped
                : _options.DefaultProfile;

        if (string.IsNullOrWhiteSpace(profileName) || !_options.Profiles.TryGetValue(profileName, out var profile))
        {
            return ExecutionProfileResolution.Failed(
                profileName, null, $"Execution profile '{profileName}' is not configured.");
        }

        var adapterKey = profile.Adapter;

        if (string.IsNullOrWhiteSpace(adapterKey))
        {
            return ExecutionProfileResolution.Failed(
                profileName, null, $"Execution profile '{profileName}' does not name an adapter.");
        }

        if (_options.Adapters.TryGetValue(adapterKey, out var adapterOptions) && !adapterOptions.Enabled)
        {
            return ExecutionProfileResolution.Failed(
                profileName, adapterKey, $"Execution adapter '{adapterKey}' (profile '{profileName}') is disabled.");
        }

        if (!_registry.TryGet(adapterKey, out var adapter) || adapter is null)
        {
            return ExecutionProfileResolution.Failed(
                profileName, adapterKey,
                $"Execution adapter '{adapterKey}' (profile '{profileName}') is not registered.");
        }

        return new ExecutionProfileResolution(true, profileName, adapterKey, adapter, null);
    }
}

public sealed record ExecutionProfileResolution(
    bool IsResolved,
    string? ProfileName,
    string? AdapterKey,
    IExecutionAdapter? Adapter,
    string? FailureReason)
{
    public static ExecutionProfileResolution Failed(string? profileName, string? adapterKey, string reason) =>
        new(false, profileName, adapterKey, null, reason);
}
