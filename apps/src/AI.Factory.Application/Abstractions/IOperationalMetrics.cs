namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Thread-safe in-process counters/timers — no Prometheus, no persisted raw
/// samples. A restart resets everything; this is a live operational
/// snapshot, not a historical record (the audit trail is that).
/// </summary>
public interface IOperationalMetrics
{
    void RecordOperationStarted();

    void RecordOperationCompleted(bool success);

    void RecordAgentExecution(bool success, long? durationMs);

    void RecordBuildExecution(bool success, long? durationMs);

    void RecordTestExecution(bool success, long? durationMs);

    void RecordRepairAttempt(bool success, long? durationMs);

    void RecordApproval(bool approved);

    void RecordRetryAttempt();

    void RecordRetrySuccess();

    void RecordRetryExhausted();

    void RecordRetryCancelled();

    void RecordAttemptTimeout();

    void RecordOperationTimeout();

    void RecordCircuitOpened();

    void RecordCircuitClosed();

    void RecordCircuitHalfOpen();

    void RecordCircuitRejected();

    void RecordBulkheadEntered();

    void RecordBulkheadQueued();

    void RecordBulkheadRejected();

    void RecordBulkheadCompleted();

    void RecordShutdownInitiated();

    void RecordWorkerStopping();

    void RecordWorkerStopped();

    void RecordWorkerPollFailure();

    void RecordWorkerDegraded();

    void RecordWorkerRecovered();

    void RecordWorkerFaulted();

    void RecordOperationsDrained(int count);

    void RecordOperationsCancelled(int count);

    void RecordShutdownTimedOut();

    void RecordShutdownRequestRejected();

    void RecordRateLimitRejected();

    void RecordSecurityRequestRejected();

    void RecordOperationalAccessDenied();

    void RecordConfigurationValidationFailed();

    void RecordSecretResolutionFailed();

    void RecordRequiredSecretMissing();

    void RecordDatabaseOperation(bool success, long? durationMs = null);

    void RecordDatabaseRetry();

    void RecordDatabaseTransientFailure();

    void RecordDatabaseConcurrencyConflict();

    void RecordDatabaseBusyFailure();

    void RecordDatabaseLockedFailure();

    void RecordDatabaseTransactionFailure();

    void RecordBackupSucceeded();

    void RecordBackupFailed();

    void RecordBackupValidated();

    void RecordRestoreSucceeded();

    void RecordRestoreFailed();

    void RecordAiRequestStarted();

    void RecordAiRequestSucceeded(long? durationMs);

    void RecordAiRequestFailed();

    void RecordAiProviderFailure();

    void RecordAiRateLimited();

    void RecordAiRoutingFallback();

    // Phase 18.1.6 — AgentRun lifecycle counters. Deliberately separate from
    // the existing RecordAgentExecution(success, durationMs) (IAgentExecutor-
    // level, unchanged) — these answer "how many runs reached each terminal
    // status" rather than a flat success/failure split.
    void RecordAgentRunCreated();

    void RecordAgentRunStarted();

    void RecordAgentRunSucceeded();

    void RecordAgentRunFailed();

    void RecordAgentRunTimedOut();

    void RecordAgentRunCancelled();

    void RecordAgentWorkerClaimSuccess();

    OperationalMetricsSnapshot GetSnapshot();
}

public sealed record OperationalMetricsSnapshot(
    DateTime GeneratedAtUtc,
    long OperationsStarted,
    long OperationsSucceeded,
    long OperationsFailed,
    long AgentExecutions,
    long AgentFailures,
    long BuildExecutions,
    long BuildFailures,
    long TestExecutions,
    long TestFailures,
    long RepairAttempts,
    long RepairSuccesses,
    long RepairFailures,
    long ApprovalApproved,
    long ApprovalRejected,
    double? AverageAgentExecutionDurationMs,
    double? AverageBuildDurationMs,
    double? AverageTestDurationMs,
    double? AverageRepairDurationMs,
    long? TotalAgentExecutionDurationMs,
    long? TotalBuildDurationMs,
    long? TotalTestDurationMs,
    long? TotalRepairDurationMs,
    long RetryAttempts = 0,
    long RetrySucceeded = 0,
    long RetryExhausted = 0,
    long RetryCancelled = 0,
    long AttemptTimeouts = 0,
    long OperationTimeouts = 0,
    long CircuitOpened = 0,
    long CircuitClosed = 0,
    long CircuitHalfOpen = 0,
    long CircuitRejected = 0,
    long BulkheadEntered = 0,
    long BulkheadQueued = 0,
    long BulkheadRejected = 0,
    long BulkheadCompleted = 0,
    long ShutdownInitiated = 0,
    long WorkerStopping = 0,
    long WorkerStopped = 0,
    long WorkerPollFailures = 0,
    long WorkerDegraded = 0,
    long WorkerRecovered = 0,
    long WorkerFaulted = 0,
    long OperationsDrained = 0,
    long OperationsCancelled = 0,
    long ShutdownTimedOut = 0,
    long ShutdownRequestsRejected = 0,
    long RateLimitRejected = 0,
    long SecurityRequestRejected = 0,
    long OperationalAccessDenied = 0,
    long ConfigurationValidationFailed = 0,
    long SecretResolutionFailed = 0,
    long RequiredSecretMissing = 0,
    long DatabaseOperations = 0,
    long DatabaseFailures = 0,
    long DatabaseRetries = 0,
    long DatabaseTransientFailures = 0,
    long DatabaseConcurrencyConflicts = 0,
    long DatabaseBusyFailures = 0,
    long DatabaseLockedFailures = 0,
    long DatabaseTransactionFailures = 0,
    double? AverageDatabaseOperationDurationMs = null,
    long? TotalDatabaseOperationDurationMs = null,
    long BackupSucceeded = 0,
    long BackupFailed = 0,
    long BackupValidated = 0,
    long RestoreSucceeded = 0,
    long RestoreFailed = 0,
    long AiRequests = 0,
    long AiRequestsSucceeded = 0,
    long AiRequestsFailed = 0,
    long AiProviderFailures = 0,
    long AiRateLimited = 0,
    long AiRoutingFallbacks = 0,
    double? AverageAiRequestDurationMs = null,
    long? TotalAiRequestDurationMs = null,
    long AgentRunsCreated = 0,
    long AgentRunsStarted = 0,
    long AgentRunsSucceeded = 0,
    long AgentRunsFailed = 0,
    long AgentRunsTimedOut = 0,
    long AgentRunsCancelled = 0,
    long AgentWorkerClaimSuccess = 0);

public sealed class OperationalMetrics : IOperationalMetrics
{
    private long _operationsStarted;
    private long _operationsSucceeded;
    private long _operationsFailed;

    private long _databaseOperations;
    private long _databaseFailures;
    private long _databaseRetries;
    private long _databaseTransientFailures;
    private long _databaseConcurrencyConflicts;
    private long _databaseBusyFailures;
    private long _databaseLockedFailures;
    private long _databaseTransactionFailures;
    private readonly DurationAccumulator _databaseDuration = new();

    private long _agentExecutions;
    private long _agentFailures;
    private readonly DurationAccumulator _agentDuration = new();

    private long _buildExecutions;
    private long _buildFailures;
    private readonly DurationAccumulator _buildDuration = new();

    private long _testExecutions;
    private long _testFailures;
    private readonly DurationAccumulator _testDuration = new();

    private long _repairAttempts;
    private long _repairSuccesses;
    private long _repairFailures;
    private readonly DurationAccumulator _repairDuration = new();

    private long _approvalApproved;
    private long _approvalRejected;

    private long _retryAttempts;
    private long _retrySucceeded;
    private long _retryExhausted;
    private long _retryCancelled;
    private long _attemptTimeouts;
    private long _operationTimeouts;

    private long _circuitOpened;
    private long _circuitClosed;
    private long _circuitHalfOpen;
    private long _circuitRejected;

    private long _bulkheadEntered;
    private long _bulkheadQueued;
    private long _bulkheadRejected;
    private long _bulkheadCompleted;

    private long _shutdownInitiated;
    private long _workerStopping;
    private long _workerStopped;
    private long _workerPollFailures;
    private long _workerDegraded;
    private long _workerRecovered;
    private long _workerFaulted;
    private long _operationsDrained;
    private long _operationsCancelled;
    private long _shutdownTimedOut;
    private long _shutdownRequestsRejected;
    private long _rateLimitRejected;
    private long _securityRequestRejected;
    private long _operationalAccessDenied;
    private long _configurationValidationFailed;
    private long _secretResolutionFailed;
    private long _requiredSecretMissing;

    private long _backupSucceeded;
    private long _backupFailed;
    private long _backupValidated;
    private long _restoreSucceeded;
    private long _restoreFailed;

    private long _aiRequests;
    private long _aiRequestsSucceeded;
    private long _aiRequestsFailed;
    private long _aiProviderFailures;
    private long _aiRateLimited;
    private long _aiRoutingFallbacks;
    private readonly DurationAccumulator _aiRequestDuration = new();

    private long _agentRunsCreated;
    private long _agentRunsStarted;
    private long _agentRunsSucceeded;
    private long _agentRunsFailed;
    private long _agentRunsTimedOut;
    private long _agentRunsCancelled;
    private long _agentWorkerClaimSuccess;

    public void RecordOperationStarted() => Interlocked.Increment(ref _operationsStarted);

    public void RecordOperationCompleted(bool success)
    {
        if (success)
        {
            Interlocked.Increment(ref _operationsSucceeded);
        }
        else
        {
            Interlocked.Increment(ref _operationsFailed);
        }
    }

    public void RecordAgentExecution(bool success, long? durationMs)
    {
        Interlocked.Increment(ref _agentExecutions);

        if (!success)
        {
            Interlocked.Increment(ref _agentFailures);
        }

        _agentDuration.Add(durationMs);
    }

    public void RecordBuildExecution(bool success, long? durationMs)
    {
        Interlocked.Increment(ref _buildExecutions);

        if (!success)
        {
            Interlocked.Increment(ref _buildFailures);
        }

        _buildDuration.Add(durationMs);
    }

    public void RecordTestExecution(bool success, long? durationMs)
    {
        Interlocked.Increment(ref _testExecutions);

        if (!success)
        {
            Interlocked.Increment(ref _testFailures);
        }

        _testDuration.Add(durationMs);
    }

    public void RecordRepairAttempt(bool success, long? durationMs)
    {
        Interlocked.Increment(ref _repairAttempts);

        if (success)
        {
            Interlocked.Increment(ref _repairSuccesses);
        }
        else
        {
            Interlocked.Increment(ref _repairFailures);
        }

        _repairDuration.Add(durationMs);
    }

    public void RecordApproval(bool approved)
    {
        if (approved)
        {
            Interlocked.Increment(ref _approvalApproved);
        }
        else
        {
            Interlocked.Increment(ref _approvalRejected);
        }
    }

    public void RecordRetryAttempt() => Interlocked.Increment(ref _retryAttempts);

    public void RecordRetrySuccess() => Interlocked.Increment(ref _retrySucceeded);

    public void RecordRetryExhausted() => Interlocked.Increment(ref _retryExhausted);

    public void RecordRetryCancelled() => Interlocked.Increment(ref _retryCancelled);

    public void RecordAttemptTimeout() => Interlocked.Increment(ref _attemptTimeouts);

    public void RecordOperationTimeout() => Interlocked.Increment(ref _operationTimeouts);

    public void RecordCircuitOpened() => Interlocked.Increment(ref _circuitOpened);

    public void RecordCircuitClosed() => Interlocked.Increment(ref _circuitClosed);

    public void RecordCircuitHalfOpen() => Interlocked.Increment(ref _circuitHalfOpen);

    public void RecordCircuitRejected() => Interlocked.Increment(ref _circuitRejected);

    public void RecordBulkheadEntered() => Interlocked.Increment(ref _bulkheadEntered);

    public void RecordBulkheadQueued() => Interlocked.Increment(ref _bulkheadQueued);

    public void RecordBulkheadRejected() => Interlocked.Increment(ref _bulkheadRejected);

    public void RecordBulkheadCompleted() => Interlocked.Increment(ref _bulkheadCompleted);

    public void RecordShutdownInitiated() => Interlocked.Increment(ref _shutdownInitiated);

    public void RecordWorkerStopping() => Interlocked.Increment(ref _workerStopping);

    public void RecordWorkerStopped() => Interlocked.Increment(ref _workerStopped);

    public void RecordWorkerPollFailure() => Interlocked.Increment(ref _workerPollFailures);

    public void RecordWorkerDegraded() => Interlocked.Increment(ref _workerDegraded);

    public void RecordWorkerRecovered() => Interlocked.Increment(ref _workerRecovered);

    public void RecordWorkerFaulted() => Interlocked.Increment(ref _workerFaulted);

    public void RecordOperationsDrained(int count) => Interlocked.Add(ref _operationsDrained, count);

    public void RecordOperationsCancelled(int count) => Interlocked.Add(ref _operationsCancelled, count);

    public void RecordShutdownTimedOut() => Interlocked.Increment(ref _shutdownTimedOut);

    public void RecordShutdownRequestRejected() => Interlocked.Increment(ref _shutdownRequestsRejected);

    public void RecordRateLimitRejected() => Interlocked.Increment(ref _rateLimitRejected);

    public void RecordSecurityRequestRejected() => Interlocked.Increment(ref _securityRequestRejected);

    public void RecordOperationalAccessDenied() => Interlocked.Increment(ref _operationalAccessDenied);
    public void RecordConfigurationValidationFailed() => Interlocked.Increment(ref _configurationValidationFailed);
    public void RecordSecretResolutionFailed() => Interlocked.Increment(ref _secretResolutionFailed);
    public void RecordRequiredSecretMissing() => Interlocked.Increment(ref _requiredSecretMissing);

    public void RecordDatabaseOperation(bool success, long? durationMs = null)
    {
        Interlocked.Increment(ref _databaseOperations);
        if (!success)
        {
            Interlocked.Increment(ref _databaseFailures);
        }
        _databaseDuration.Add(durationMs);
    }

    public void RecordDatabaseRetry() => Interlocked.Increment(ref _databaseRetries);
    public void RecordDatabaseTransientFailure() => Interlocked.Increment(ref _databaseTransientFailures);
    public void RecordDatabaseConcurrencyConflict() => Interlocked.Increment(ref _databaseConcurrencyConflicts);
    public void RecordDatabaseBusyFailure() => Interlocked.Increment(ref _databaseBusyFailures);
    public void RecordDatabaseLockedFailure() => Interlocked.Increment(ref _databaseLockedFailures);
    public void RecordDatabaseTransactionFailure() => Interlocked.Increment(ref _databaseTransactionFailures);

    public void RecordBackupSucceeded() => Interlocked.Increment(ref _backupSucceeded);
    public void RecordBackupFailed() => Interlocked.Increment(ref _backupFailed);
    public void RecordBackupValidated() => Interlocked.Increment(ref _backupValidated);
    public void RecordRestoreSucceeded() => Interlocked.Increment(ref _restoreSucceeded);
    public void RecordRestoreFailed() => Interlocked.Increment(ref _restoreFailed);

    public void RecordAiRequestStarted() => Interlocked.Increment(ref _aiRequests);

    public void RecordAiRequestSucceeded(long? durationMs)
    {
        Interlocked.Increment(ref _aiRequestsSucceeded);
        _aiRequestDuration.Add(durationMs);
    }

    public void RecordAiRequestFailed() => Interlocked.Increment(ref _aiRequestsFailed);
    public void RecordAiProviderFailure() => Interlocked.Increment(ref _aiProviderFailures);
    public void RecordAiRateLimited() => Interlocked.Increment(ref _aiRateLimited);
    public void RecordAiRoutingFallback() => Interlocked.Increment(ref _aiRoutingFallbacks);

    public void RecordAgentRunCreated() => Interlocked.Increment(ref _agentRunsCreated);
    public void RecordAgentRunStarted() => Interlocked.Increment(ref _agentRunsStarted);
    public void RecordAgentRunSucceeded() => Interlocked.Increment(ref _agentRunsSucceeded);
    public void RecordAgentRunFailed() => Interlocked.Increment(ref _agentRunsFailed);
    public void RecordAgentRunTimedOut() => Interlocked.Increment(ref _agentRunsTimedOut);
    public void RecordAgentRunCancelled() => Interlocked.Increment(ref _agentRunsCancelled);
    public void RecordAgentWorkerClaimSuccess() => Interlocked.Increment(ref _agentWorkerClaimSuccess);

    public OperationalMetricsSnapshot GetSnapshot()
    {
        return new OperationalMetricsSnapshot(
            DateTime.UtcNow,
            Interlocked.Read(ref _operationsStarted),
            Interlocked.Read(ref _operationsSucceeded),
            Interlocked.Read(ref _operationsFailed),
            Interlocked.Read(ref _agentExecutions),
            Interlocked.Read(ref _agentFailures),
            Interlocked.Read(ref _buildExecutions),
            Interlocked.Read(ref _buildFailures),
            Interlocked.Read(ref _testExecutions),
            Interlocked.Read(ref _testFailures),
            Interlocked.Read(ref _repairAttempts),
            Interlocked.Read(ref _repairSuccesses),
            Interlocked.Read(ref _repairFailures),
            Interlocked.Read(ref _approvalApproved),
            Interlocked.Read(ref _approvalRejected),
            _agentDuration.Average,
            _buildDuration.Average,
            _testDuration.Average,
            _repairDuration.Average,
            _agentDuration.Total,
            _buildDuration.Total,
            _testDuration.Total,
            _repairDuration.Total,
            Interlocked.Read(ref _retryAttempts),
            Interlocked.Read(ref _retrySucceeded),
            Interlocked.Read(ref _retryExhausted),
            Interlocked.Read(ref _retryCancelled),
            Interlocked.Read(ref _attemptTimeouts),
            Interlocked.Read(ref _operationTimeouts),
            Interlocked.Read(ref _circuitOpened),
            Interlocked.Read(ref _circuitClosed),
            Interlocked.Read(ref _circuitHalfOpen),
            Interlocked.Read(ref _circuitRejected),
            Interlocked.Read(ref _bulkheadEntered),
            Interlocked.Read(ref _bulkheadQueued),
            Interlocked.Read(ref _bulkheadRejected),
            Interlocked.Read(ref _bulkheadCompleted),
            Interlocked.Read(ref _shutdownInitiated),
            Interlocked.Read(ref _workerStopping),
            Interlocked.Read(ref _workerStopped),
            Interlocked.Read(ref _workerPollFailures),
            Interlocked.Read(ref _workerDegraded),
            Interlocked.Read(ref _workerRecovered),
            Interlocked.Read(ref _workerFaulted),
            Interlocked.Read(ref _operationsDrained),
            Interlocked.Read(ref _operationsCancelled),
            Interlocked.Read(ref _shutdownTimedOut),
            Interlocked.Read(ref _shutdownRequestsRejected),
            Interlocked.Read(ref _rateLimitRejected),
            Interlocked.Read(ref _securityRequestRejected),
            Interlocked.Read(ref _operationalAccessDenied),
            Interlocked.Read(ref _configurationValidationFailed),
            Interlocked.Read(ref _secretResolutionFailed),
            Interlocked.Read(ref _requiredSecretMissing),
            Interlocked.Read(ref _databaseOperations),
            Interlocked.Read(ref _databaseFailures),
            Interlocked.Read(ref _databaseRetries),
            Interlocked.Read(ref _databaseTransientFailures),
            Interlocked.Read(ref _databaseConcurrencyConflicts),
            Interlocked.Read(ref _databaseBusyFailures),
            Interlocked.Read(ref _databaseLockedFailures),
            Interlocked.Read(ref _databaseTransactionFailures),
            _databaseDuration.Average,
            _databaseDuration.Total,
            Interlocked.Read(ref _backupSucceeded),
            Interlocked.Read(ref _backupFailed),
            Interlocked.Read(ref _backupValidated),
            Interlocked.Read(ref _restoreSucceeded),
            Interlocked.Read(ref _restoreFailed),
            Interlocked.Read(ref _aiRequests),
            Interlocked.Read(ref _aiRequestsSucceeded),
            Interlocked.Read(ref _aiRequestsFailed),
            Interlocked.Read(ref _aiProviderFailures),
            Interlocked.Read(ref _aiRateLimited),
            Interlocked.Read(ref _aiRoutingFallbacks),
            _aiRequestDuration.Average,
            _aiRequestDuration.Total,
            Interlocked.Read(ref _agentRunsCreated),
            Interlocked.Read(ref _agentRunsStarted),
            Interlocked.Read(ref _agentRunsSucceeded),
            Interlocked.Read(ref _agentRunsFailed),
            Interlocked.Read(ref _agentRunsTimedOut),
            Interlocked.Read(ref _agentRunsCancelled),
            Interlocked.Read(ref _agentWorkerClaimSuccess));
    }

    /// <summary>Count + running total, so average is derived rather than stored — no raw samples kept.</summary>
    private sealed class DurationAccumulator
    {
        private long _count;
        private long _totalMs;

        public void Add(long? durationMs)
        {
            if (durationMs is null)
            {
                return;
            }

            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _totalMs, durationMs.Value);
        }

        public long? Total => Interlocked.Read(ref _count) == 0 ? null : Interlocked.Read(ref _totalMs);

        public double? Average
        {
            get
            {
                var count = Interlocked.Read(ref _count);
                return count == 0 ? null : (double)Interlocked.Read(ref _totalMs) / count;
            }
        }
    }
}
