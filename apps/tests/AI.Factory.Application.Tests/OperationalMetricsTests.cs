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

    // Phase L — the in-process AI-request counters lost their only producer in Phase K (the runtimes
    // make every model request). The fields stay in GET /api/operations/metrics for contract
    // compatibility, honestly unmeasured: 0 / null, never a fabricated count.
    [Fact]
    public void AiRequestFields_AreRetainedInTheMetricsContract_AsUnmeasured()
    {
        var metrics = new OperationalMetrics();
        metrics.RecordRestoreFailed();
        metrics.RecordAgentRunCreated();

        var snapshot = metrics.GetSnapshot();
        var json = System.Text.Json.JsonSerializer.Serialize(
            snapshot, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(0, snapshot.AiRequests);
        Assert.Equal(0, snapshot.AiRoutingFallbacks);
        Assert.Null(snapshot.AverageAiRequestDurationMs);
        Assert.Null(snapshot.TotalAiRequestDurationMs);
        // The counters on either side of the retained block still land in their own fields.
        Assert.Equal(1, snapshot.RestoreFailed);
        Assert.Equal(1, snapshot.AgentRunsCreated);
        Assert.Contains("\"aiRoutingFallbacks\":0", json);
        Assert.Contains("\"aiRequests\":0", json);
        Assert.Contains("\"totalAiRequestDurationMs\":null", json);
    }
}
