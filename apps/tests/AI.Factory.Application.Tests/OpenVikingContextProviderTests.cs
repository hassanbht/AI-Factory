using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Context;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class OpenVikingContextProviderTests
{
    private sealed class FakeOpenVikingClient : IOpenVikingClient
    {
        private readonly Func<OpenVikingQueryRequestDto, OpenVikingQueryResponseDto> _respond;
        private readonly Exception? _throwOnQuery;

        public FakeOpenVikingClient(Func<OpenVikingQueryRequestDto, OpenVikingQueryResponseDto> respond)
        {
            _respond = respond;
        }

        public FakeOpenVikingClient(Exception throwOnQuery)
        {
            _throwOnQuery = throwOnQuery;
            _respond = _ => throw new InvalidOperationException("unreachable");
        }

        public int CallCount { get; private set; }

        public Task<OpenVikingQueryResponseDto> QueryAsync(
            OpenVikingQueryRequestDto request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            cancellationToken.ThrowIfCancellationRequested();

            if (_throwOnQuery is not null)
            {
                throw _throwOnQuery;
            }

            return Task.FromResult(_respond(request));
        }
    }

    private static OpenVikingOptions MakeOptions(bool enabled) =>
        new() { Enabled = enabled, BaseUrl = "http://unused.invalid/", TimeoutSeconds = 5 };

    [Fact]
    public async Task QueryAsync_ReturnsEmpty_WithoutCallingClient_WhenDisabled()
    {
        var client = new FakeOpenVikingClient(_ => new OpenVikingQueryResponseDto());
        var provider = new OpenVikingContextProvider(
            client, MakeOptions(enabled: false), NullLogger<OpenVikingContextProvider>.Instance);

        var result = await provider.QueryAsync(
            new ContextQuery(Guid.NewGuid(), ContextCategory.ArchitectureDocuments));

        Assert.Empty(result.Documents);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task QueryAsync_MapsDocuments_WhenEnabledAndClientSucceeds()
    {
        var client = new FakeOpenVikingClient(_ => new OpenVikingQueryResponseDto
        {
            Documents = new List<OpenVikingDocumentDto>
            {
                new()
                {
                    Source = "kb:pattern-1",
                    Content = "Use dependency injection.",
                    Relevance = 0.9,
                    Metadata = new Dictionary<string, string> { ["topic"] = "patterns" }
                }
            }
        });

        var provider = new OpenVikingContextProvider(
            client, MakeOptions(enabled: true), NullLogger<OpenVikingContextProvider>.Instance);

        var result = await provider.QueryAsync(
            new ContextQuery(Guid.NewGuid(), ContextCategory.CodingStandards));

        Assert.Single(result.Documents);
        Assert.Equal("kb:pattern-1", result.Documents[0].Source);
        Assert.Equal(0.9, result.Documents[0].Relevance);
        Assert.Equal("patterns", result.Documents[0].Metadata["topic"]);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task QueryAsync_FallsBackToEmpty_WhenClientThrows()
    {
        var client = new FakeOpenVikingClient(new HttpRequestException("connection refused"));
        var provider = new OpenVikingContextProvider(
            client, MakeOptions(enabled: true), NullLogger<OpenVikingContextProvider>.Instance);

        var result = await provider.QueryAsync(
            new ContextQuery(Guid.NewGuid(), ContextCategory.ArchitectureDocuments));

        Assert.Empty(result.Documents);
    }

    [Fact]
    public async Task QueryAsync_PropagatesCancellation_RatherThanFallingBack()
    {
        var client = new FakeOpenVikingClient(_ => new OpenVikingQueryResponseDto());
        var provider = new OpenVikingContextProvider(
            client, MakeOptions(enabled: true), NullLogger<OpenVikingContextProvider>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.QueryAsync(
                new ContextQuery(Guid.NewGuid(), ContextCategory.ArchitectureDocuments),
                cts.Token));
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmpty_WhenClientReturnsNoDocuments()
    {
        var client = new FakeOpenVikingClient(_ => new OpenVikingQueryResponseDto());
        var provider = new OpenVikingContextProvider(
            client, MakeOptions(enabled: true), NullLogger<OpenVikingContextProvider>.Instance);

        var result = await provider.QueryAsync(
            new ContextQuery(Guid.NewGuid(), ContextCategory.ProjectDecisions));

        Assert.Empty(result.Documents);
    }
}
