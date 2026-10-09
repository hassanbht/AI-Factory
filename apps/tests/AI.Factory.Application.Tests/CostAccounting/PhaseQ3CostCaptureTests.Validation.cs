using AI.Factory.Application.Abstractions;
using AI.Factory.Application.CostAccounting;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests.CostAccounting;

public sealed partial class PhaseQ3CostCaptureTests
{
    [Fact]
    public void MissingGenerationId_YieldsNoCapture()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var captured = OpenRouterGenerationCapture.CaptureFromResponse(
            new Dictionary<string, string>(), """{"model":"m","usage":{"prompt_tokens":3}}""", false);
        Assert.Empty(captured);
    }

    [Fact]
    public void MalformedSseFrames_AreSkipped_AndMissingTerminalUsageYieldsNothing()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var body = ": comment\nnot-a-frame\n" + "data: {not json}\n" + "data: [DONE]\n";
        Assert.Empty(OpenRouterGenerationCapture.CaptureFromResponse(null, body, true));

        var noTerminal = "data: {\"model\":\"m\",\"usage\":{\"prompt_tokens\":2}}\n" + "data: [DONE]\n";
        Assert.Empty(OpenRouterGenerationCapture.CaptureFromResponse(null, noTerminal, true));

        var terminal = "data: {\"id\":\"gen-sse-term1\",\"model\":\"m\",\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1,\"total_tokens\":3}}\n"
            + "data: [DONE]\n";
        var captured = OpenRouterGenerationCapture.CaptureFromResponse(null, terminal, true);
        Assert.Single(captured);
        Assert.Equal("gen-sse-term1", captured[0].GenerationId);
    }

    [Theory]
    [InlineData(200, """{"no":"data"}""")]
    [InlineData(200, """{"data":{"id":"gen-other-1","model":"m","total_cost":0.1}}""")]
    [InlineData(200, """not json""")]
    public void MalformedOrMismatchedMetadata_FailsClosed(int status, string body)
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var result = OpenRouterGenerationUsageValidator.Validate("gen-expected-1", status, body);
        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData("""{"data":{"id":"gen-cost-1","model":"m","total_cost":null}}""")]
    [InlineData("""{"data":{"id":"gen-cost-1","model":"m"}}""")]
    [InlineData("""{"data":{"id":"gen-cost-1","model":"m","total_cost":"nonsense"}}""")]
    [InlineData("""{"data":{"id":"gen-cost-1","model":"m","total_cost":-0.5}}""")]
    public void UnusableTotalCost_FailsClosed_AsTransient(string body)
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var result = OpenRouterGenerationUsageValidator.Validate("gen-cost-1", 200, body);
        Assert.False(result.Ok);
        Assert.True(result.IsTransient);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(401, false)]
    [InlineData(402, false)]
    [InlineData(404, false)]
    public void ProviderStatuses_Classified_TransientOrTerminal(int status, bool expectTransient)
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var result = OpenRouterGenerationUsageValidator.Validate("gen-status-1", status, """{"error":{"code":1}}""");
        Assert.False(result.Ok);
        Assert.Equal(expectTransient, result.IsTransient);
    }
}
