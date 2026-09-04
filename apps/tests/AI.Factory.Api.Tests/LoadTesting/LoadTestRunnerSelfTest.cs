using Xunit;

namespace AI.Factory.Api.Tests.LoadTesting;

[Trait("Category", "Load")]
public sealed class LoadTestRunnerSelfTest
{
    [Fact]
    public async Task RunAsync_WithTotalOperations_RunsExactlyThatManyTimes_NoHttp()
    {
        var counter = 0;
        var runner = new LoadTestRunner();
        var scenario = new LoadTestScenario("noop", async ct =>
        {
            Interlocked.Increment(ref counter);
            await Task.Delay(1, ct);
        });

        var result = await runner.RunAsync(scenario, new LoadTestConfiguration
        {
            Concurrency = 50,
            TotalOperations = 100
        });

        Assert.Equal(100, counter);
        Assert.Equal(100, result.Metrics.TotalOperations);
        Assert.Equal(100, result.Metrics.SuccessfulOperations);
    }
}
