using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Gateways;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

/// <summary>
/// Phase J/K — unit-level guarantees behind the end-to-end propagation tests: one status
/// mapping, no guessed gateway reason, redacted and bounded upstream messages, fail-closed
/// profile routing and one endpoint per gateway at startup, and no retry of a failure the
/// runtime itself reported.
/// </summary>
public sealed class GatewayOperationalReadinessTests
{
    private const string FakeKey = "sk-or-v1-0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(401, ModelGatewayFailureReason.Authentication)]
    [InlineData(402, ModelGatewayFailureReason.Billing)]
    [InlineData(403, ModelGatewayFailureReason.Authorization)]
    [InlineData(404, ModelGatewayFailureReason.ModelUnavailable)]
    [InlineData(408, ModelGatewayFailureReason.Timeout)]
    [InlineData(429, ModelGatewayFailureReason.RateLimited)]
    [InlineData(400, ModelGatewayFailureReason.InvalidRequest)]
    [InlineData(422, ModelGatewayFailureReason.InvalidRequest)]
    [InlineData(500, ModelGatewayFailureReason.ProviderUnavailable)]
    [InlineData(502, ModelGatewayFailureReason.GatewayUnavailable)]
    [InlineData(503, ModelGatewayFailureReason.GatewayUnavailable)]
    [InlineData(504, ModelGatewayFailureReason.Timeout)]
    [InlineData(418, ModelGatewayFailureReason.Unknown)]
    public void FromHttpStatus_IsTheSingleDeterministicMapping(int status, ModelGatewayFailureReason expected)
    {
        Assert.Equal(expected, ModelGatewayFailureClassifier.FromHttpStatus(status));
        Assert.Equal(expected, ModelGatewayFailureClassifier.FromRuntimeMessage($"upstream said HTTP {status}: no"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Process exited with code 402.")]
    [InlineData("error code 401 somewhere in a test log")]
    [InlineData("OpenHands conversation transitioned to error state: error")]
    [InlineData("HTTP 200: fine")]
    [InlineData("HTTP ٤٠٢: non-ASCII digits")]
    public void FromRuntimeMessage_WithoutAnExplicitHttpStatus_IsNotAvailable_NeverGuessed(string? message)
    {
        Assert.Null(ModelGatewayFailureClassifier.FromRuntimeMessage(message));
    }

    [Fact]
    public void FromRuntimeMessage_TheTerminalSummary_TheLastStatus_Wins()
    {
        Assert.Equal(
            ModelGatewayFailureReason.Billing,
            ModelGatewayFailureClassifier.FromRuntimeMessage("tool fetched HTTP 404 earlier\nBilling or credits exhausted: HTTP 402: no credits"));
    }

    [Fact]
    public void SanitizeUpstreamMessage_RedactsSecrets_AndBoundsLength()
    {
        var message = $"Authorization: Bearer {FakeKey} api_key={FakeKey} " + new string('x', 2_000);

        var sanitized = ModelGatewayFailureClassifier.SanitizeUpstreamMessage(message);

        Assert.DoesNotContain(FakeKey, sanitized);
        Assert.EndsWith("...[truncated]", sanitized);
        Assert.True(sanitized.Length <= ModelGatewayFailureClassifier.MaxUpstreamMessageLength + "...[truncated]".Length);
    }

    [Theory]
    [InlineData("OmniRoute", "is disabled")]
    [InlineData("Nope", "is not configured")]
    public void Validate_ProfileRoutedToDisabledOrUnknownGateway_FailsStartup(string gateway, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GatewayOptions().Validate(Execution(gateway)));
        Assert.Contains(expected, ex.Message);
        Assert.Contains("'Review'", ex.Message);
    }

    [Fact]
    public void Validate_ProfilesRoutedToEnabledGateways_OrToNone_Pass()
    {
        GatewayOptions().Validate(Execution("OpenRouter"));
        GatewayOptions().Validate(Execution(null));
        GatewayOptions().Validate();
    }

    [Theory]
    [InlineData("OmniRoute")]
    [InlineData("Nope")]
    public void Validate_DefaultGatewayThatIsDisabledOrUnknown_FailsStartup(string defaultGateway)
    {
        var options = GatewayOptions();
        options.DefaultGateway = defaultGateway;

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("must name a configured, enabled model gateway", ex.Message);
    }

    [Theory]
    [InlineData("{\"detail\":{\"api_key\":\"omni-7f3a9c2e5b8d1f4a\"}}", "omni-7f3a9c2e5b8d1f4a")]
    [InlineData("{\"password\":\"p@ss w0rd\"}", "w0rd")]
    [InlineData("{'api_key': 'abc def ghi'}", "def")]
    public void SanitizeUpstreamMessage_RedactsQuotedJsonSecretValues_Entirely(string message, string leaked)
    {
        Assert.DoesNotContain(leaked, ModelGatewayFailureClassifier.SanitizeUpstreamMessage(message));
    }

    [Fact]
    public void Validate_CatalogProviderPointingAtAnotherEndpointThanItsGateway_FailsStartup()
    {
        var catalog = new AiProviderOptions();
        catalog.Providers["openrouter"] = new ProviderCredentialSettings { BaseUrl = "http://localhost:20128/v1" };

        var ex = Assert.Throws<InvalidOperationException>(() => GatewayOptions().Validate(Execution("OpenRouter"), catalog));
        Assert.Contains("One endpoint per gateway", ex.Message);
    }

    [Fact]
    public void Validate_CatalogProviderOnTheGatewayEndpoint_OrDisabled_OrUnrelated_Passes()
    {
        var catalog = new AiProviderOptions();
        catalog.Providers["OpenRouter"] = new ProviderCredentialSettings { BaseUrl = "https://openrouter.ai/api/v1/" };
        catalog.Providers["omniroute"] = new ProviderCredentialSettings { BaseUrl = "http://elsewhere:1/v1" }; // gateway disabled
        catalog.Providers["krouter"] = new ProviderCredentialSettings { BaseUrl = "http://elsewhere:2/v1" };   // no such gateway

        GatewayOptions().Validate(Execution("OpenRouter"), catalog);
    }

    [Theory]
    [InlineData("Hermes reported a failed run (exit code 0): connection refused by upstream, HTTP 503")]
    [InlineData("Hermes reported a failed run (exit code 0): Billing or credits exhausted: HTTP 402")]
    public void RuntimeReportedFailure_WithExitCodeZero_IsNeverATransientLaunchFailure(string error)
    {
        var result = new AgentExecutionResult(false, "out", error, 5, ExitCode: 0);

        Assert.False(AgentExecutionFailureClassifier.IsTransientLaunchFailure(result));
    }

    private static ModelGatewayOptions GatewayOptions() => new()
    {
        DefaultGateway = "OpenRouter",
        Gateways =
        {
            ["OpenRouter"] = new GatewayEntryOptions { BaseUrl = OpenRouterGateway.DefaultBaseUrl },
            ["OmniRoute"] = new GatewayEntryOptions { BaseUrl = OmniRouteGateway.DefaultBaseUrl, Enabled = false }
        }
    };

    private static ExecutionOptions Execution(string? reviewGateway)
    {
        var execution = new ExecutionOptions { DefaultProfile = "Hermes" };
        execution.Profiles["Hermes"] = new ExecutionProfileOptions { Adapter = "Hermes", Gateway = "OpenRouter" };
        execution.Profiles["Review"] = new ExecutionProfileOptions { Adapter = "Hermes", Gateway = reviewGateway };
        return execution;
    }
}
