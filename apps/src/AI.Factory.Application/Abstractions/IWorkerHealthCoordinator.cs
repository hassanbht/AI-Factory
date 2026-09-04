namespace AI.Factory.Application.Abstractions;

public enum WorkerLifecycleState
{
    Starting = 1,
    Ready = 2,
    Stopping = 3,
    Faulted = 4,
    Degraded = 5
}

public sealed record WorkerHealthSnapshot(
    WorkerLifecycleState State,
    bool IsReady,
    bool IsLive,
    DateTime StartedAtUtc,
    string? LastError = null,
    int ConsecutiveFailures = 0);

/// <summary>
/// Coordinates background worker lifecycle states and health signals for container orchestrators.
/// </summary>
public interface IWorkerHealthCoordinator
{
    WorkerHealthSnapshot GetSnapshot();
    void MarkReady();
    void MarkStopping();
    void MarkFaulted(Exception ex);
    void ReportPollFailure(Exception ex);
    void ReportPollSuccess();
}
