using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.29.1 §C — the check that would have caught release blocker 2.
///
/// "auto/coding:free" satisfied every offline check in
/// <see cref="ModelRoutingOptions.Validate"/>: non-empty id, known provider, every
/// category resolvable, not one of the six known placeholder ids. It was still
/// unservable, and OpenRouter answered HTTP 400 for every single agent run. Only a
/// provider-aware check can tell those apart.
/// </summary>
public sealed class ModelRoutingPreflightTests
{
    private sealed class StubCatalog : IProviderModelCatalog
    {
        private readonly ProviderModelCatalogResult _result;

        public StubCatalog(string provider, ProviderModelCatalogResult result)
        {
            Provider = provider;
            _result = result;
        }

        public string Provider { get; }

        public int CallCount { get; private set; }

        public Task<ProviderModelCatalogResult> GetAvailableModelIdsAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private static ModelRoutingOptions RoutingWith(params string[] modelIds)
    {
        var options = new ModelRoutingOptions
        {
            Models = modelIds
                .Select(id => new ModelCapability { ModelId = id, Provider = "openrouter" })
                .ToList()
        };

        options.CategoryProfiles["coding"] = new List<string> { modelIds[0] };
        options.AgentTypeCategories["Implementation"] = "coding";

        return options;
    }

    private static ModelRoutingPreflight Build(
        ModelRoutingOptions routing,
        ModelRoutingPreflightOptions preflight,
        params IProviderModelCatalog[] catalogs) =>
        new(routing, preflight, catalogs, NullLogger<ModelRoutingPreflight>.Instance);

    [Fact]
    public async Task Disabled_DoesNotCallTheProvider()
    {
        var catalog = new StubCatalog(
            "openrouter", ProviderModelCatalogResult.Available(new[] { "qwen/qwen3-coder" }));

        var preflight = Build(
            RoutingWith("qwen/qwen3-coder"),
            new ModelRoutingPreflightOptions { Enabled = false },
            catalog);

        var report = await preflight.RunAsync();

        Assert.False(report.Executed);
        Assert.Equal(0, catalog.CallCount);
    }

    [Fact]
    public async Task ServableModel_IsVerified()
    {
        var preflight = Build(
            RoutingWith("qwen/qwen3-coder"),
            new ModelRoutingPreflightOptions { Enabled = true },
            new StubCatalog("openrouter", ProviderModelCatalogResult.Available(
                new[] { "qwen/qwen3-coder", "deepseek/deepseek-v4-flash-0731:free" })));

        var report = await preflight.RunAsync();

        Assert.True(report.Executed);
        Assert.False(report.HasUnservableModel);
        Assert.Equal("qwen/qwen3-coder", Assert.Single(report.Verified).ModelId);
    }

    [Fact]
    public async Task UnservableModel_IsReportedWithTheRolesItBreaks()
    {
        var preflight = Build(
            RoutingWith("auto/coding:free"),
            new ModelRoutingPreflightOptions { Enabled = true },
            new StubCatalog("openrouter", ProviderModelCatalogResult.Available(
                new[] { "qwen/qwen3-coder", "openrouter/auto" })));

        var report = await preflight.RunAsync();

        Assert.True(report.HasUnservableModel);

        var unservable = Assert.Single(report.Unservable);
        Assert.Equal("auto/coding:free", unservable.ModelId);
        Assert.Equal("openrouter", unservable.Provider);
        Assert.Contains("coding", unservable.Categories);

        // The message has to name the model and say the configuration is being
        // rejected — an operator reading a startup crash needs to know it is their
        // config, not a transient failure they should retry.
        var message = report.BuildFailureMessage();
        Assert.Contains("auto/coding:free", message);
        Assert.Contains("openrouter", message);
        Assert.Contains("not available from provider", message);
    }

    [Fact]
    public async Task ProviderOutage_IsNotReportedAsAnInvalidModel()
    {
        var preflight = Build(
            RoutingWith("qwen/qwen3-coder"),
            new ModelRoutingPreflightOptions { Enabled = true },
            new StubCatalog("openrouter", ProviderModelCatalogResult.Unavailable("connection refused")));

        var report = await preflight.RunAsync();

        // The whole point: an outage proves nothing about a model id. Treating it as
        // "model invalid" would turn every provider blip into an unbootable
        // deployment and would send operators to fix configuration that is correct.
        Assert.False(report.HasUnservableModel);
        Assert.Empty(report.Verified);

        var unreachable = Assert.Single(report.UnreachableProviders);
        Assert.Equal("openrouter", unreachable.Provider);
        Assert.Contains("connection refused", unreachable.Reason);
    }

    [Fact]
    public async Task ProviderWithoutACatalog_IsReportedRatherThanSilentlyPassed()
    {
        var preflight = Build(
            RoutingWith("some/model"),
            new ModelRoutingPreflightOptions { Enabled = true });

        var report = await preflight.RunAsync();

        Assert.False(report.HasUnservableModel);
        Assert.Equal("openrouter", Assert.Single(report.ProvidersWithoutCatalog));
    }

    [Fact]
    public async Task OneLookupPerProvider_NotPerModel()
    {
        var catalog = new StubCatalog("openrouter", ProviderModelCatalogResult.Available(
            new[] { "qwen/qwen3-coder", "deepseek/deepseek-v4-flash-0731:free" }));

        var preflight = Build(
            RoutingWith("qwen/qwen3-coder", "deepseek/deepseek-v4-flash-0731:free"),
            new ModelRoutingPreflightOptions { Enabled = true },
            catalog);

        var report = await preflight.RunAsync();

        Assert.Equal(2, report.Verified.Count);
        Assert.Equal(1, catalog.CallCount);
    }

    [Fact]
    public async Task ModelIdMatching_IsCaseInsensitive()
    {
        var preflight = Build(
            RoutingWith("Qwen/Qwen3-Coder"),
            new ModelRoutingPreflightOptions { Enabled = true },
            new StubCatalog("openrouter", ProviderModelCatalogResult.Available(
                new[] { "qwen/qwen3-coder" })));

        var report = await preflight.RunAsync();

        Assert.False(report.HasUnservableModel);
    }
}
