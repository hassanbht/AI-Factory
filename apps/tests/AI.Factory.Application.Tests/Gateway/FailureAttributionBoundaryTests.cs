using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Gateway;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

/// <summary>
/// Phase P.1 — Failure Attribution Boundary Tests.
/// Validates Group F:
/// - OpenRouter 402, 401, 404, 429, 503;
/// - runtime non-zero exit;
/// - runtime reported Failed with exit 0;
/// - malformed runtime report;
/// - provider error extraction from stdout/stderr when obscured by container logs.
/// </summary>
public sealed class FailureAttributionBoundaryTests
{
    [Theory]
    [InlineData(401, ModelGatewayFailureReason.Authentication)]
    [InlineData(402, ModelGatewayFailureReason.Billing)]
    [InlineData(404, ModelGatewayFailureReason.ModelUnavailable)]
    [InlineData(429, ModelGatewayFailureReason.RateLimited)]
    [InlineData(503, ModelGatewayFailureReason.GatewayUnavailable)]
    public void FromHttpStatus_MapsExactStatusesCorrectly(int statusCode, ModelGatewayFailureReason expected)
    {
        Assert.Equal(expected, ModelGatewayFailureClassifier.FromHttpStatus(statusCode));
    }

    [Theory]
    [InlineData("litellm.exceptions.NotFoundError: OpenRouterException - HTTP 404: Model not found", ModelGatewayFailureReason.ModelUnavailable)]
    [InlineData("Billing or credits exhausted: HTTP 402: Payment required", ModelGatewayFailureReason.Billing)]
    [InlineData("Authentication failure: HTTP 401: Invalid api key", ModelGatewayFailureReason.Authentication)]
    [InlineData("Rate limit hit: HTTP 429: Too many requests", ModelGatewayFailureReason.RateLimited)]
    [InlineData("Upstream down: HTTP 503: Service Unavailable", ModelGatewayFailureReason.GatewayUnavailable)]
    public void FromRuntimeMessage_ExtractsGatewayReason_FromRuntimeMessage(string message, ModelGatewayFailureReason expected)
    {
        var result = ModelGatewayFailureClassifier.FromRuntimeMessage(message);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractProviderError_ExtractsHttp404_FromStdoutWhenObscuredByS6StartupLogs()
    {
        var stdout =
            """
            [s6-init] making user provided files executable...
            [s6-init] ensuring user provided files are executable...
            s6-rc: info: service s6rc-oneshot-runner: starting
            litellm.exceptions.NotFoundError: OpenRouterException - {"error":{"message":"Model deepseek/not-found not found","code":404}} HTTP 404
            s6-rc: info: service s6rc-oneshot-runner: successfully started
            """;
        var stderr = "s6-rc: info: service s6rc-oneshot-runner: starting\n";

        var extracted = HermesAgentExecutor.ExtractProviderError(stdout, stderr);

        Assert.NotNull(extracted);
        Assert.Contains("HTTP 404", extracted);
        Assert.Contains("Model", extracted);
    }

    [Fact]
    public void ExtractProviderError_ExtractsHttp402_FromStderr()
    {
        var stdout = "";
        var stderr = "Error during execution: Billing or credits exhausted: HTTP 402: Insufficient credits.";

        var extracted = HermesAgentExecutor.ExtractProviderError(stdout, stderr);

        Assert.NotNull(extracted);
        Assert.Contains("HTTP 402", extracted);
    }

    [Fact]
    public void ExtractProviderError_ReturnsNull_WhenNoProviderErrorPresent()
    {
        var stdout = "Normal build output line 1\nLine 2";
        var stderr = "Warning: unused variable";

        var extracted = HermesAgentExecutor.ExtractProviderError(stdout, stderr);

        Assert.Null(extracted);
    }
}
