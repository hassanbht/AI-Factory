using System.Net;
using System.Text;
using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Isolated integration test: exercises the real HTTP transport
/// (HttpOpenVikingClient / OpenVikingContextProvider) against a local
/// HttpListener-backed server. No real OpenViking product exists to
/// integrate against yet (per .ai/context/decisions.md, it's a future
/// layer) — this is the closest honest equivalent: real sockets, real
/// JSON (de)serialization, a stand-in backend, same spirit as the Docker
/// integration test using the real docker binary.
/// </summary>
public sealed class OpenVikingIntegrationTests
{
    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task ServeOnceAsync(HttpListener listener, int statusCode, string body)
    {
        var context = await listener.GetContextAsync();
        var buffer = Encoding.UTF8.GetBytes(body);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.OutputStream.Close();
    }

    [Fact]
    public async Task HttpOpenVikingClient_ParsesRealResponse_FromLocalServer()
    {
        var port = GetFreeTcpPort();
        var prefix = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var responseJson =
            """{"documents":[{"source":"kb:pattern-1","content":"Use dependency injection.","relevance":0.9,"metadata":{"topic":"patterns"}}]}""";

        var serverTask = ServeOnceAsync(listener, 200, responseJson);

        using var httpClient = new HttpClient { BaseAddress = new Uri(prefix) };
        var client = new HttpOpenVikingClient(
            httpClient, new OpenVikingOptions { Enabled = true, BaseUrl = prefix, TimeoutSeconds = 5 });

        var response = await client.QueryAsync(new OpenVikingQueryRequestDto
        {
            Category = "ArchitectureDocuments",
            ProjectId = Guid.NewGuid(),
            MaxResults = 5
        });

        await serverTask;
        listener.Stop();

        Assert.Single(response.Documents);
        Assert.Equal("kb:pattern-1", response.Documents[0].Source);
        Assert.Equal("Use dependency injection.", response.Documents[0].Content);
        Assert.Equal(0.9, response.Documents[0].Relevance);
        Assert.Equal("patterns", response.Documents[0].Metadata!["topic"]);
    }

    [Fact]
    public async Task HttpOpenVikingClient_Throws_OnServerError()
    {
        var port = GetFreeTcpPort();
        var prefix = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serverTask = ServeOnceAsync(listener, 500, "{}");

        using var httpClient = new HttpClient { BaseAddress = new Uri(prefix) };
        var client = new HttpOpenVikingClient(
            httpClient, new OpenVikingOptions { Enabled = true, BaseUrl = prefix, TimeoutSeconds = 5 });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.QueryAsync(new OpenVikingQueryRequestDto
            {
                Category = "ArchitectureDocuments",
                ProjectId = Guid.NewGuid(),
                MaxResults = 5
            }));

        await serverTask;
        listener.Stop();
    }

    [Fact]
    public async Task OpenVikingContextProvider_ReturnsMappedDocuments_ThroughRealHttpTransport()
    {
        var port = GetFreeTcpPort();
        var prefix = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var responseJson =
            """{"documents":[{"source":"kb:decision-1","content":"Use SQLite for workflow state.","relevance":1.0,"metadata":{}}]}""";

        var serverTask = ServeOnceAsync(listener, 200, responseJson);

        using var httpClient = new HttpClient { BaseAddress = new Uri(prefix) };
        var options = new OpenVikingOptions { Enabled = true, BaseUrl = prefix, TimeoutSeconds = 5 };
        var client = new HttpOpenVikingClient(httpClient, options);
        var provider = new OpenVikingContextProvider(client, options, NullLogger<OpenVikingContextProvider>.Instance);

        var result = await provider.QueryAsync(
            new ContextQuery(Guid.NewGuid(), ContextCategory.ProjectDecisions));

        await serverTask;
        listener.Stop();

        Assert.Single(result.Documents);
        Assert.Equal("kb:decision-1", result.Documents[0].Source);
    }

    [Fact]
    public async Task HttpOpenVikingClient_RetriesTransientFailure_ThenSucceeds()
    {
        var port = GetFreeTcpPort();
        var prefix = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var responseJson =
            """{"documents":[{"source":"kb:pattern-2","content":"Retried successfully.","relevance":0.8,"metadata":{}}]}""";

        var serverTask = Task.Run(async () =>
        {
            await ServeOnceAsync(listener, 503, "{}");
            await ServeOnceAsync(listener, 200, responseJson);
        });

        using var httpClient = new HttpClient { BaseAddress = new Uri(prefix) };
        var retryExecutor = new RetryExecutor(
            new RetryOptions { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) },
            NullLogger<RetryExecutor>.Instance);
        var client = new HttpOpenVikingClient(
            httpClient, new OpenVikingOptions { Enabled = true, BaseUrl = prefix, TimeoutSeconds = 5 }, retryExecutor);

        var response = await client.QueryAsync(new OpenVikingQueryRequestDto
        {
            Category = "ArchitectureDocuments",
            ProjectId = Guid.NewGuid(),
            MaxResults = 5
        });

        await serverTask;
        listener.Stop();

        Assert.Single(response.Documents);
        Assert.Equal("kb:pattern-2", response.Documents[0].Source);
    }

    [Fact]
    public async Task HttpOpenVikingClient_DoesNotRetry_OnNonTransientStatusCode()
    {
        var port = GetFreeTcpPort();
        var prefix = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        // Only ONE request is ever served — if the client incorrectly
        // retried a 400, the second GetContextAsync() would hang forever
        // and this test would time out instead of failing fast.
        var serverTask = ServeOnceAsync(listener, 400, "{}");

        using var httpClient = new HttpClient { BaseAddress = new Uri(prefix) };
        var retryExecutor = new RetryExecutor(
            new RetryOptions { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) },
            NullLogger<RetryExecutor>.Instance);
        var client = new HttpOpenVikingClient(
            httpClient, new OpenVikingOptions { Enabled = true, BaseUrl = prefix, TimeoutSeconds = 5 }, retryExecutor);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.QueryAsync(new OpenVikingQueryRequestDto
            {
                Category = "ArchitectureDocuments",
                ProjectId = Guid.NewGuid(),
                MaxResults = 5
            }));

        await serverTask;
        listener.Stop();
    }

    [Fact]
    public async Task OpenVikingContextProvider_FallsBackToEmpty_WhenServerUnreachable()
    {
        // Nothing is listening on this port.
        var port = GetFreeTcpPort();
        var prefix = $"http://localhost:{port}/";

        using var httpClient = new HttpClient { BaseAddress = new Uri(prefix) };
        var options = new OpenVikingOptions { Enabled = true, BaseUrl = prefix, TimeoutSeconds = 2 };
        var client = new HttpOpenVikingClient(httpClient, options);
        var provider = new OpenVikingContextProvider(client, options, NullLogger<OpenVikingContextProvider>.Instance);

        var result = await provider.QueryAsync(
            new ContextQuery(Guid.NewGuid(), ContextCategory.ArchitectureDocuments));

        Assert.Empty(result.Documents);
    }
}
