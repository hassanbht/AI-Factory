using AI.Factory.Application.Abstractions.Execution;

namespace AI.Factory.Application.Execution;

/// <summary>
/// Default <see cref="IExecutionAdapterRegistry"/>, populated from every
/// <see cref="IExecutionAdapter"/> registered in DI. Construction fails with
/// <see cref="DuplicateExecutionAdapterException"/> on a repeated key (including
/// keys that differ only in case), so an ambiguous registration can never
/// resolve to "whichever came last".
/// </summary>
public sealed class ExecutionAdapterRegistry : IExecutionAdapterRegistry
{
    private readonly Dictionary<string, IExecutionAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);

    public ExecutionAdapterRegistry(IEnumerable<IExecutionAdapter> adapters)
    {
        foreach (var adapter in adapters)
        {
            if (string.IsNullOrWhiteSpace(adapter.Key))
            {
                throw new ArgumentException(
                    $"Execution adapter '{adapter.GetType().Name}' has an empty key.", nameof(adapters));
            }

            if (!_adapters.TryAdd(adapter.Key, adapter))
            {
                throw new DuplicateExecutionAdapterException(adapter.Key);
            }
        }

        Keys = _adapters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> Keys { get; }

    public bool TryGet(string key, out IExecutionAdapter? adapter)
    {
        adapter = null;
        return !string.IsNullOrWhiteSpace(key) && _adapters.TryGetValue(key, out adapter);
    }
}
