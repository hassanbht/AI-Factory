using System.Net;
using System.Text.Json;
using AI.Factory.Infrastructure.Agents.OpenHands;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

public sealed class OpenHandsClientTests
{
    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task Client_InjectsSessionApiKeyHeader()
    {
        var stub = new DelegatingHandlerStub(req =>
        {
            Assert.True(req.Headers.Contains("X-Session-API-Key"));
            Assert.Equal("my-secret-session-key", req.Headers.GetValues("X-Session-API-Key").First());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"profiles\":[]}")
            };
        });

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:8000") };
        var client = new OpenHandsClient(httpClient, "my-secret-session-key", NullLogger<OpenHandsClient>.Instance);

        var isHealthy = await client.CheckHealthAsync();
        Assert.True(isHealthy);
    }

    [Fact]
    public async Task CreateConversationAsync_SerializesRequestAndDeserializesResponse()
    {
        var stub = new DelegatingHandlerStub(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("/api/conversations", req.RequestUri?.AbsolutePath);

            var json = req.Content?.ReadAsStringAsync().Result ?? string.Empty;
            Assert.Contains("LocalWorkspace", json);
            Assert.Contains("/workspace", json);
            Assert.Contains("CodeActAgent", json);
            Assert.Contains("openrouter/deepseek", json);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new OpenHandsConversationInfo
                {
                    Id = "conv-abc-456",
                    ExecutionStatus = "idle"
                }))
            };
        });

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:8000") };
        var client = new OpenHandsClient(httpClient, null, NullLogger<OpenHandsClient>.Instance);

        var result = await client.CreateConversationAsync(new OpenHandsCreateConversationRequest
        {
            Workspace = new OpenHandsWorkspaceDto { WorkingDir = "/workspace" },
            AgentSettings = new OpenHandsAgentSettingsDto
            {
                Agent = "CodeActAgent",
                Llm = new OpenHandsLlmDto { Model = "openrouter/deepseek", ApiKey = "k" }
            }
        });

        Assert.Equal("conv-abc-456", result.Id);
        Assert.Equal("idle", result.ExecutionStatus);
    }

    [Fact]
    public async Task SendMessageAsync_PostsToEventsEndpointWithRunFlag()
    {
        var stub = new DelegatingHandlerStub(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("/api/conversations/conv-123/events", req.RequestUri?.AbsolutePath);

            var json = req.Content?.ReadAsStringAsync().Result ?? string.Empty;
            Assert.Contains("\"run\":true", json);
            Assert.Contains("Hello test agent", json);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}")
            };
        });

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:8000") };
        var client = new OpenHandsClient(httpClient, null, NullLogger<OpenHandsClient>.Instance);

        var success = await client.SendMessageAsync("conv-123", "Hello test agent", run: true);
        Assert.True(success);
    }

    [Fact]
    public async Task InterruptAsync_PostsToInterruptEndpoint()
    {
        var stub = new DelegatingHandlerStub(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("/api/conversations/conv-123/interrupt", req.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}")
            };
        });

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:8000") };
        var client = new OpenHandsClient(httpClient, null, NullLogger<OpenHandsClient>.Instance);

        var success = await client.InterruptAsync("conv-123");
        Assert.True(success);
    }

    [Fact]
    public async Task DeleteConversationAsync_SendsHttpDelete()
    {
        var stub = new DelegatingHandlerStub(req =>
        {
            Assert.Equal(HttpMethod.Delete, req.Method);
            Assert.Equal("/api/conversations/conv-123", req.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}")
            };
        });

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:8000") };
        var client = new OpenHandsClient(httpClient, null, NullLogger<OpenHandsClient>.Instance);

        var success = await client.DeleteConversationAsync("conv-123");
        Assert.True(success);
    }
}
