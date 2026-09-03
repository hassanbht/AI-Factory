using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class SecretProviderTests
{
    [Fact]
    public async Task EnvironmentSecretProvider_ResolvesExistingEnvironmentVariable()
    {
        var testKey = "TEST_FACTORY_SECRET_" + Guid.NewGuid().ToString("N");
        var testVal = "SecretValue_12345";
        Environment.SetEnvironmentVariable(testKey, testVal);

        try
        {
            var provider = new EnvironmentSecretProvider(NullLogger<EnvironmentSecretProvider>.Instance);

            var result = await provider.GetSecretAsync(testKey);
            var resolution = await provider.ResolveSecretAsync(testKey);

            Assert.Equal(testVal, result);
            Assert.True(resolution.Found);
            Assert.Equal(testVal, resolution.Value);
            Assert.Equal(EnvironmentSecretProvider.ProviderIdentifier, resolution.ProviderName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(testKey, null);
        }
    }

    [Fact]
    public async Task EnvironmentSecretProvider_MissingSecret_ReturnsNullAndNotFound()
    {
        var missingKey = "NON_EXISTENT_SECRET_" + Guid.NewGuid().ToString("N");
        var provider = new EnvironmentSecretProvider(NullLogger<EnvironmentSecretProvider>.Instance);

        var result = await provider.GetSecretAsync(missingKey);
        var resolution = await provider.ResolveSecretAsync(missingKey);

        Assert.Null(result);
        Assert.False(resolution.Found);
        Assert.Null(resolution.Value);
    }

    [Fact]
    public async Task EnvironmentSecretProvider_ThrowsOnCancelledToken()
    {
        var provider = new EnvironmentSecretProvider(NullLogger<EnvironmentSecretProvider>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.GetSecretAsync("ANY_KEY", cts.Token));
    }

    [Fact]
    public async Task InMemorySecretProvider_ResolvesAndSetsSecrets()
    {
        var provider = new InMemorySecretProvider(new Dictionary<string, string>
        {
            ["SECRET_A"] = "ValueA"
        });

        Assert.Equal("ValueA", await provider.GetSecretAsync("SECRET_A"));

        provider.SetSecret("SECRET_B", "ValueB");
        var resolutionB = await provider.ResolveSecretAsync("SECRET_B");

        Assert.True(resolutionB.Found);
        Assert.Equal("ValueB", resolutionB.Value);
        Assert.Equal(InMemorySecretProvider.ProviderIdentifier, resolutionB.ProviderName);

        var missing = await provider.ResolveSecretAsync("MISSING");
        Assert.False(missing.Found);
        Assert.Null(missing.Value);
    }

    [Fact]
    public async Task CompositeSecretProvider_ResolvesInPriorityOrder()
    {
        var provider1 = new InMemorySecretProvider(new Dictionary<string, string>
        {
            ["SHARED_KEY"] = "FirstProviderValue",
            ["KEY_ONE"] = "One"
        });

        var provider2 = new InMemorySecretProvider(new Dictionary<string, string>
        {
            ["SHARED_KEY"] = "SecondProviderValue",
            ["KEY_TWO"] = "Two"
        });

        var composite = new CompositeSecretProvider(
            new ISecretProvider[] { provider1, provider2 },
            NullLogger<CompositeSecretProvider>.Instance);

        // First provider takes precedence
        var sharedVal = await composite.GetSecretAsync("SHARED_KEY");
        Assert.Equal("FirstProviderValue", sharedVal);

        // Fallback to second provider
        var twoVal = await composite.GetSecretAsync("KEY_TWO");
        Assert.Equal("Two", twoVal);

        // Missing from all providers
        var missingResolution = await composite.ResolveSecretAsync("COMPLETELY_MISSING");
        Assert.False(missingResolution.Found);
        Assert.Null(missingResolution.Value);
    }
}
