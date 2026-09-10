using System.Net;
using System.Net.Http.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class OpenAiCompatibleModelProviderTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request);
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHttpMessageHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _exception;
    }

    private sealed class InMemorySecretProviderStub : ISecretProvider
    {
        private readonly string? _value;
        public string ProviderName => "InMemoryStub";
        public InMemorySecretProviderStub(string? value) => _value = value;

        public Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default) =>
            Task.FromResult(_value);

        public Task<SecretResolutionResult> ResolveSecretAsync(string secretName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretResolutionResult(_value is not null, _value, ProviderName, DateTime.UtcNow));
    }

    private static AiCompletionRequest Request() =>
        new(Guid.NewGuid(), AiCapability.CodeGeneration, "system prompt", "user prompt", PreferredModelId: "model-1");

    private OpenAiCompatibleModelProvider BuildProvider(HttpMessageHandler handler, string? apiKeySecretValue = "secret-key")
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://provider.invalid/") };
        return new OpenAiCompatibleModelProvider(
            "openrouter", new[] { "CodeGeneration" }, httpClient,
            new InMemorySecretProviderStub(apiKeySecretValue), "SOME_API_KEY",
            NullLogger<OpenAiCompatibleModelProvider>.Instance);
    }

    [Fact]
    public async Task CompleteAsync_MapsSuccessResponse_ExtractsContentAndUsage()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                choices = new[] { new { message = new { role = "assistant", content = "generated code" } } },
                usage = new { prompt_tokens = 12, completion_tokens = 34 }
            })
        });

        var provider = BuildProvider(handler);
        var result = await provider.CompleteAsync(Request());

        Assert.True(result.Success);
        Assert.Equal("generated code", result.Content);
        Assert.Equal("openrouter", result.ProviderId);
        Assert.Equal("model-1", result.ModelId);
        Assert.Equal(12, result.PromptTokens);
        Assert.Equal(34, result.CompletionTokens);
    }

    [Fact]
    public async Task CompleteAsync_SendsBearerAuthorizationHeader_FromResolvedSecret()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { choices = Array.Empty<object>() })
        });

        var provider = BuildProvider(handler, apiKeySecretValue: "sk-real-secret-value");
        await provider.CompleteAsync(Request());

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-real-secret-value", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("model-1", handler.LastRequestBody);
    }

    [Fact]
    public async Task CompleteAsync_MapsTooManyRequests_ToRateLimitedWithRetryAfter()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        var provider = BuildProvider(handler);
        var result = await provider.CompleteAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(AiFailureCategory.RateLimited, result.FailureCategory);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiFailureCategory.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, AiFailureCategory.Authentication)]
    [InlineData(HttpStatusCode.BadRequest, AiFailureCategory.InvalidRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiFailureCategory.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, AiFailureCategory.ProviderFailure)]
    public async Task CompleteAsync_MapsHttpStatusCode_ToNormalizedCategory(HttpStatusCode statusCode, AiFailureCategory expectedCategory)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode));
        var provider = BuildProvider(handler);

        var result = await provider.CompleteAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(expectedCategory, result.FailureCategory);
    }

    [Fact]
    public async Task CompleteAsync_MapsTaskCanceledException_ToTimeout()
    {
        var handler = new ThrowingHttpMessageHandler(new TaskCanceledException("simulated client timeout"));
        var provider = BuildProvider(handler);

        var result = await provider.CompleteAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(AiFailureCategory.Timeout, result.FailureCategory);
    }

    [Fact]
    public async Task CompleteAsync_MapsCallerCancellation_ToCancelled()
    {
        var handler = new ThrowingHttpMessageHandler(new OperationCanceledException());
        var provider = BuildProvider(handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await provider.CompleteAsync(Request(), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(AiFailureCategory.Cancelled, result.FailureCategory);
    }

    [Fact]
    public async Task CompleteAsync_MapsNetworkFailure_ToProviderFailure()
    {
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection reset"));
        var provider = BuildProvider(handler);

        var result = await provider.CompleteAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(AiFailureCategory.ProviderFailure, result.FailureCategory);
    }

    [Fact]
    public async Task CompleteAsync_ReturnsAuthenticationFailure_WithoutLeakingSecret_WhenSecretMissing()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = BuildProvider(handler, apiKeySecretValue: null);

        var result = await provider.CompleteAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(AiFailureCategory.Authentication, result.FailureCategory);
        Assert.DoesNotContain("sk-", result.ErrorMessage ?? string.Empty);
        Assert.Null(handler.LastRequest); // Never even sent the HTTP call
    }
}
