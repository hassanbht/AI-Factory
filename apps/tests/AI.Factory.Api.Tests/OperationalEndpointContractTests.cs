using System.Net;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Phase 17.4.11 Finding 3 — pins the canonical operational diagnostics route
/// contract so the production runbook cannot silently drift from the actual
/// API again. A 404 here means the documented path no longer resolves to a
/// registered route; every canonical path must also require the operational
/// access key (401 without one), proving rate limiting/security middleware
/// still applies.
/// </summary>
public sealed class OperationalEndpointContractTests : IClassFixture<OperationalSecurityTestFactory>
{
    private readonly HttpClient _client;

    public OperationalEndpointContractTests(OperationalSecurityTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    public static IEnumerable<object[]> CanonicalRoutes()
    {
        yield return new object[] { "/api/operations/metrics" };
        yield return new object[] { "/api/operations/resilience" };
        yield return new object[] { "/api/operations/recent" };
        yield return new object[] { "/api/operations/active" };
        yield return new object[] { "/api/operations/configuration" };
        yield return new object[] { "/api/operations/database" };
        yield return new object[] { "/api/operations/deployment" };
    }

    [Theory]
    [MemberData(nameof(CanonicalRoutes))]
    public async Task CanonicalOperationalRoute_Exists_AndRequiresAccessKey(string path)
    {
        var response = await _client.GetAsync(path);

        // A missing/renamed route responds 404; an existing-but-unauthenticated
        // route responds 401. Only the latter is the documented contract.
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
