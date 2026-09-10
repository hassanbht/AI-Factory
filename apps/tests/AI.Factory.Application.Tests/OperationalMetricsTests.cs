using AI.Factory.Application.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class OperationalMetricsTests
{
    [Fact]
    public void RecordOperationStarted_IncrementsOperationsStartedCounter()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordOperationStarted();
        metrics.RecordOperationStarted();

        Assert.Equal(2, metrics.GetSnapshot().OperationsStarted);
    }

    [Fact]
    public void RecordOperationCompleted_SplitsIntoSucceededAndFailedCounters()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordOperationCompleted(success: true);
        metrics.RecordOperationCompleted(success: true);
        metrics.RecordOperationCompleted(success: false);

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(2, snapshot.OperationsSucceeded);
        Assert.Equal(1, snapshot.OperationsFailed);
    }

    [Fact]
    public void RecordAgentExecution_TracksExecutionsAndFailuresSeparately()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordAgentExecution(success: true, durationMs: 100);
        metrics.RecordAgentExecution(success: false, durationMs: 200);

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(2, snapshot.AgentExecutions);
        Assert.Equal(1, snapshot.AgentFailures);
    }

    [Fact]
    public void RecordAgentExecution_AggregatesDuration_IntoAverageAndTotal()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordAgentExecution(success: true, durationMs: 100);
        metrics.RecordAgentExecution(success: true, durationMs: 300);

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(400, snapshot.TotalAgentExecutionDurationMs);
        Assert.Equal(200, snapshot.AverageAgentExecutionDurationMs);
    }

    [Fact]
    public void GetSnapshot_WithNoDurationSamples_ReturnsNullAverage_NeverZeroFabricated()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordBuildExecution(success: true, durationMs: null);

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(1, snapshot.BuildExecutions);
        Assert.Null(snapshot.AverageBuildDurationMs);
        Assert.Null(snapshot.TotalBuildDurationMs);
    }

    [Fact]
    public void RecordApproval_TracksApprovedAndRejectedSeparately()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordApproval(approved: true);
        metrics.RecordApproval(approved: false);
        metrics.RecordApproval(approved: false);

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(1, snapshot.ApprovalApproved);
        Assert.Equal(2, snapshot.ApprovalRejected);
    }

    [Fact]
    public void AiMetrics_TrackRequestsSuccessesFailuresFallbacksAndRateLimits_Independently()
    {
        var metrics = new OperationalMetrics();

        metrics.RecordAiRequestStarted();
        metrics.RecordAiRequestStarted();
        metrics.RecordAiRequestSucceeded(120);
        metrics.RecordAiRequestFailed();
        metrics.RecordAiProviderFailure();
        metrics.RecordAiRateLimited();
        metrics.RecordAiRoutingFallback();

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(2, snapshot.AiRequests);
        Assert.Equal(1, snapshot.AiRequestsSucceeded);
        Assert.Equal(1, snapshot.AiRequestsFailed);
        Assert.Equal(1, snapshot.AiProviderFailures);
        Assert.Equal(1, snapshot.AiRateLimited);
        Assert.Equal(1, snapshot.AiRoutingFallbacks);
        Assert.Equal(120, snapshot.AverageAiRequestDurationMs);
        Assert.Equal(120, snapshot.TotalAiRequestDurationMs);
    }

    [Fact]
    public void AiMetrics_ConcurrentIncrements_ProduceExactCount()
    {
        var metrics = new OperationalMetrics();

        Parallel.For(0, 1000, _ => metrics.RecordAiRoutingFallback());

        Assert.Equal(1000, metrics.GetSnapshot().AiRoutingFallbacks);
    }
}
