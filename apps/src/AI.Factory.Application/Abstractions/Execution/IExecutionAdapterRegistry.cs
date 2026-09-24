namespace AI.Factory.Application.Abstractions.Execution;

/// <summary>
/// Keyed lookup of the registered <see cref="IExecutionAdapter"/>s. Keys are
/// unique (case-insensitive); an unknown key is reported as not found — never
/// substituted by another adapter.
/// </summary>
public interface IExecutionAdapterRegistry
{
    bool TryGet(string key, out IExecutionAdapter? adapter);

    /// <summary>Registered keys in ordinal order (deterministic, for diagnostics).</summary>
    IReadOnlyList<string> Keys { get; }
}

public sealed class DuplicateExecutionAdapterException : Exception
{
    public DuplicateExecutionAdapterException(string key)
        : base($"Execution adapter '{key}' is already registered.")
    {
    }
}
