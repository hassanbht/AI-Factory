namespace AI.Factory.Application.Resilience;

public sealed class BulkheadProfileOptions
{
    public bool Enabled { get; set; } = true;

    public int MaxConcurrentExecutions { get; set; } = 2;

    public int MaxQueueLength { get; set; } = 5;

    public BulkheadProfileOptions Clone()
    {
        return new BulkheadProfileOptions
        {
            Enabled = Enabled,
            MaxConcurrentExecutions = MaxConcurrentExecutions,
            MaxQueueLength = MaxQueueLength
        };
    }
}

public sealed record BulkheadSnapshot(
    string DependencyName,
    int ActiveExecutions,
    int QueuedExecutions,
    int MaxConcurrentExecutions,
    int MaxQueueLength,
    long TotalAcquisitions = 0,
    long TotalRejections = 0);

public interface IBulkhead
{
    string Name { get; }

    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default);

    BulkheadSnapshot GetSnapshot();
}

public interface IBulkheadRegistry
{
    IBulkhead GetOrCreate(string name, BulkheadProfileOptions? options = null);

    IBulkhead? TryGet(string name);

    IReadOnlyCollection<BulkheadSnapshot> GetAllSnapshots();
}

public sealed class BulkheadRejectedException : Exception
{
    public string DependencyName { get; }

    public int MaxConcurrentExecutions { get; }

    public int MaxQueueLength { get; }

    public int ActiveExecutions { get; }

    public int QueuedExecutions { get; }

    public BulkheadRejectedException(
        string dependencyName,
        int maxConcurrentExecutions,
        int maxQueueLength,
        int activeExecutions = 0,
        int queuedExecutions = 0)
        : base($"Dependency '{dependencyName}' bulkhead capacity exceeded (max concurrent: {maxConcurrentExecutions}, max queue: {maxQueueLength}).")
    {
        DependencyName = dependencyName;
        MaxConcurrentExecutions = maxConcurrentExecutions;
        MaxQueueLength = maxQueueLength;
        ActiveExecutions = activeExecutions;
        QueuedExecutions = queuedExecutions;
    }
}
