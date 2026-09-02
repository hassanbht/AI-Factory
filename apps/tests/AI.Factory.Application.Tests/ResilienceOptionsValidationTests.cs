using AI.Factory.Application.Resilience;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ResilienceOptionsValidationTests
{
    [Fact]
    public void DefaultProfiles_PassValidation()
    {
        var options = new ResilienceOptions();
        options.Profiles["AgentExecution"] = ResilienceOptions.CreateDefaultProfile(ResilienceOptions.AgentExecution);
        options.Profiles["ProviderCall"] = ResilienceOptions.CreateDefaultProfile(ResilienceOptions.ProviderCall);

        options.Validate(); // Should not throw
    }

    [Fact]
    public void NegativeMaxRetryAttempts_ThrowsException()
    {
        var options = new ResilienceOptions();
        options.Profiles["Invalid"] = new ResilienceProfile
        {
            MaxRetryAttempts = -1
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxRetryAttempts", ex.Message);
    }

    [Fact]
    public void NegativeInitialDelay_ThrowsException()
    {
        var options = new ResilienceOptions();
        options.Profiles["Invalid"] = new ResilienceProfile
        {
            InitialDelay = TimeSpan.FromSeconds(-1)
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("InitialDelay", ex.Message);
    }

    [Fact]
    public void MaxDelaySmallerThanInitialDelay_ThrowsException()
    {
        var options = new ResilienceOptions();
        options.Profiles["Invalid"] = new ResilienceProfile
        {
            InitialDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromSeconds(5)
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxDelay", ex.Message);
    }

    [Fact]
    public void NonPositiveAttemptTimeout_ThrowsException()
    {
        var options = new ResilienceOptions();
        options.Profiles["Invalid"] = new ResilienceProfile
        {
            AttemptTimeout = TimeSpan.Zero
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("AttemptTimeout", ex.Message);
    }

    [Fact]
    public void NonPositiveOperationTimeout_ThrowsException()
    {
        var options = new ResilienceOptions();
        options.Profiles["Invalid"] = new ResilienceProfile
        {
            OperationTimeout = TimeSpan.FromSeconds(-5)
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("OperationTimeout", ex.Message);
    }

    [Fact]
    public void AttemptTimeoutExceedsOperationTimeout_ThrowsException()
    {
        var options = new ResilienceOptions();
        options.Profiles["Invalid"] = new ResilienceProfile
        {
            AttemptTimeout = TimeSpan.FromMinutes(10),
            OperationTimeout = TimeSpan.FromMinutes(5)
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("cannot exceed", ex.Message);
    }
}
