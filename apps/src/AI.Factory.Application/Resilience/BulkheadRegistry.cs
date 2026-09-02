using System.Collections.Concurrent;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AI.Factory.Application.Resilience;

public sealed class BulkheadRegistry : IBulkheadRegistry
{
    private readonly ConcurrentDictionary<string, IBulkhead> _bulkheads = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IOperationalMetrics? _metrics;
    private readonly ILoggerFactory? _loggerFactory;

    public BulkheadRegistry(
        IServiceScopeFactory? scopeFactory = null,
        IOperationalMetrics? metrics = null,
        ILoggerFactory? loggerFactory = null)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _loggerFactory = loggerFactory;
    }

    public IBulkhead GetOrCreate(string name, BulkheadProfileOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _bulkheads.GetOrAdd(name, n =>
        {
            var opts = options ?? new BulkheadProfileOptions();
            var logger = _loggerFactory?.CreateLogger<BulkheadImpl>() ?? NullLogger<BulkheadImpl>.Instance;
            return new BulkheadImpl(n, opts, _scopeFactory, _metrics, logger);
        });
    }

    public IBulkhead? TryGet(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        _bulkheads.TryGetValue(name, out var bulkhead);
        return bulkhead;
    }

    public IReadOnlyCollection<BulkheadSnapshot> GetAllSnapshots()
    {
        return _bulkheads.Values.Select(b => b.GetSnapshot()).ToList();
    }

    internal sealed class BulkheadImpl : IBulkhead
    {
        private readonly BulkheadProfileOptions _options;
        private readonly IServiceScopeFactory? _scopeFactory;
        private readonly IOperationalMetrics? _metrics;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _semaphore;

        private int _activeCount;
        private int _queuedCount;
        private long _totalAcquisitions;
        private long _totalRejections;

        public string Name { get; }

        public BulkheadImpl(
            string name,
            BulkheadProfileOptions options,
            IServiceScopeFactory? scopeFactory,
            IOperationalMetrics? metrics,
            ILogger logger)
        {
            Name = name;
            _options = options;
            _scopeFactory = scopeFactory;
            _metrics = metrics;
            _logger = logger;
            _semaphore = new SemaphoreSlim(options.MaxConcurrentExecutions, options.MaxConcurrentExecutions);
        }

        public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_options.Enabled)
            {
                return NoOpDisposable.Instance;
            }

            // 1. Try immediate acquisition without queuing
            if (_semaphore.Wait(0, CancellationToken.None))
            {
                Interlocked.Increment(ref _activeCount);
                Interlocked.Increment(ref _totalAcquisitions);
                _metrics?.RecordBulkheadEntered();
                return new BulkheadPermit(this);
            }

            // 2. Immediate acquisition failed; check if queue capacity is available
            var currentQueued = Interlocked.Increment(ref _queuedCount);
            if (currentQueued > _options.MaxQueueLength)
            {
                Interlocked.Decrement(ref _queuedCount);
                Interlocked.Increment(ref _totalRejections);
                _metrics?.RecordBulkheadRejected();

                _logger.LogWarning(
                    "Bulkhead rejection: Dependency={Dependency}, MaxConcurrent={MaxConcurrent}, MaxQueue={MaxQueue}, Active={Active}, Queued={Queued}",
                    Name, _options.MaxConcurrentExecutions, _options.MaxQueueLength, _activeCount, currentQueued - 1);

                FireAudit(AuditEventType.BulkheadRejected, AuditSeverity.Warning,
                    $"Bulkhead queue capacity exceeded for dependency '{Name}'. Rejected.", "Rejected");

                throw new BulkheadRejectedException(
                    Name,
                    _options.MaxConcurrentExecutions,
                    _options.MaxQueueLength,
                    _activeCount,
                    _queuedCount);
            }

            // 3. Queued within capacity
            _metrics?.RecordBulkheadQueued();
            FireAudit(AuditEventType.BulkheadQueued, AuditSeverity.Info,
                $"Operation queued in bulkhead for dependency '{Name}'. Position: {currentQueued}.", "Queued");

            try
            {
                await _semaphore.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Decrement(ref _queuedCount);
                _logger.LogInformation("Cancelled while waiting in bulkhead queue for {Dependency}.", Name);
                throw;
            }

            // 4. Permit acquired after queuing
            Interlocked.Decrement(ref _queuedCount);
            Interlocked.Increment(ref _activeCount);
            Interlocked.Increment(ref _totalAcquisitions);
            _metrics?.RecordBulkheadEntered();

            return new BulkheadPermit(this);
        }

        public BulkheadSnapshot GetSnapshot()
        {
            return new BulkheadSnapshot(
                Name,
                Volatile.Read(ref _activeCount),
                Volatile.Read(ref _queuedCount),
                _options.MaxConcurrentExecutions,
                _options.MaxQueueLength,
                Interlocked.Read(ref _totalAcquisitions),
                Interlocked.Read(ref _totalRejections));
        }

        internal void Release()
        {
            if (!_options.Enabled)
            {
                return;
            }

            Interlocked.Decrement(ref _activeCount);
            _semaphore.Release();
            _metrics?.RecordBulkheadCompleted();
        }

        private void FireAudit(AuditEventType eventType, AuditSeverity severity, string message, string outcome)
        {
            if (_scopeFactory is null)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var auditLogger = scope.ServiceProvider.GetService<IAuditLogger>();
                    if (auditLogger is not null)
                    {
                        await auditLogger.LogAsync(
                            executionId: Guid.NewGuid(),
                            eventType: eventType,
                            severity: severity,
                            message: message,
                            outcome: outcome,
                            actorType: "System");
                    }
                }
                catch
                {
                    // Fire-and-forget
                }
            });
        }

        private sealed class BulkheadPermit : IDisposable
        {
            private BulkheadImpl? _bulkhead;

            public BulkheadPermit(BulkheadImpl bulkhead)
            {
                _bulkhead = bulkhead;
            }

            public void Dispose()
            {
                var bh = Interlocked.Exchange(ref _bulkhead, null);
                bh?.Release();
            }
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public static readonly NoOpDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
