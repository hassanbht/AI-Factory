using System.Collections.Concurrent;
using Xunit;

namespace AI.Factory.Api.Tests.Load;

/// <summary>
/// CorrelationMiddleware echoes an inbound X-Correlation-ID header verbatim (see
/// Middleware/CorrelationMiddleware.cs). Fires concurrent requests each with its own
/// unique correlation id and verifies no cross-request leakage/reuse.
/// </summary>
[Trait("Category", "Load")]
public sealed class CorrelationIsolationLoadTests : IClassFixture<HighThroughputApiFactory>
{
    private readonly HttpClient _client;

    public CorrelationIsolationLoadTests(HighThroughputApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ConcurrentRequests_WithUniqueCorrelationIds_NeverLeakBetweenResponses()
    {
        const int requestCount = 40;
        var sentIds = Enumerable.Range(0, requestCount).Select(i => $"load-test-{Guid.NewGuid():N}-{i}").ToList();
        var receivedIds = new ConcurrentBag<string>();

        var tasks = sentIds.Select(async sentId =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/approvals/pending");
            request.Headers.Add("X-Correlation-ID", sentId);

            var response = await _client.SendAsync(request);
            var echoed = response.Headers.GetValues("X-Correlation-ID").Single();
            receivedIds.Add(echoed);

            // The middleware echoes the sent header verbatim.
            Assert.Equal(sentId, echoed);
        });

        await Task.WhenAll(tasks);

        Assert.Equal(requestCount, receivedIds.Count);
        Assert.Equal(requestCount, receivedIds.Distinct().Count());
    }
}
