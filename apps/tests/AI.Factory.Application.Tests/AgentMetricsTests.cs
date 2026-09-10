using AI.Factory.Application.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.1.6 — the new AgentRun lifecycle counters on OperationalMetrics.</summary>
public sealed class AgentMetricsTests
{
    [Fact]
    public void AgentRunLifecycleCounters_StartAtZero()
    {
        var metrics = new OperationalMetrics();
        var snapshot = metrics.GetSnapshot();

        Assert.Equal(0, snapshot.AgentRunsCreated);
        Assert.Equal(0, snapshot.AgentRunsStarted);
        Assert.Equal(0, snapshot.AgentRunsSucceeded);
        Assert.Equal(0, snapshot.AgentRunsFailed);
        Assert.Equal(0, snapshot.AgentRunsTimedOut);
        Assert.Equal(0, snapshot.AgentRunsCancelled);
        Assert.Equal(0, snapshot.AgentWorkerClaimSuccess);
    }

    [Fact]
    public void RecordAgentRunCreated_IncrementsOnly_CreatedCounter()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordAgentRunCreated();
        metrics.RecordAgentRunCreated();

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(2, snapshot.AgentRunsCreated);
        Assert.Equal(0, snapshot.AgentRunsSucceeded);
    }

    [Fact]
    public void EachLifecycleOutcome_IncrementsItsOwnCounter_Independently()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordAgentRunStarted();
        metrics.RecordAgentRunSucceeded();
        metrics.RecordAgentRunFailed();
        metrics.RecordAgentRunTimedOut();
        metrics.RecordAgentRunCancelled();
        metrics.RecordAgentWorkerClaimSuccess();

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(1, snapshot.AgentRunsStarted);
        Assert.Equal(1, snapshot.AgentRunsSucceeded);
        Assert.Equal(1, snapshot.AgentRunsFailed);
        Assert.Equal(1, snapshot.AgentRunsTimedOut);
        Assert.Equal(1, snapshot.AgentRunsCancelled);
        Assert.Equal(1, snapshot.AgentWorkerClaimSuccess);
    }

    [Fact]
    public void AgentRunCounters_ConcurrentIncrements_ProduceExactCount()
    {
        var metrics = new OperationalMetrics();

        Parallel.For(0, 1000, _ => metrics.RecordAgentRunSucceeded());

        Assert.Equal(1000, metrics.GetSnapshot().AgentRunsSucceeded);
    }

    [Fact]
    public void RecordAgentExecution_ExistingDurationMetric_StillTracksAverageAndTotal()
    {
        // Unchanged from before this phase — the new granular counters are
        // additive, not a replacement for the existing execution-duration metric.
        var metrics = new OperationalMetrics();

        metrics.RecordAgentExecution(true, 100);
        metrics.RecordAgentExecution(true, 300);

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(200, snapshot.AverageAgentExecutionDurationMs);
        Assert.Equal(400, snapshot.TotalAgentExecutionDurationMs);
    }
}
