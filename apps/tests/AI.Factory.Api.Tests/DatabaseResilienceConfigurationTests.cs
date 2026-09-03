using AI.Factory.Application.Configuration;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class DatabaseResilienceConfigurationTests
{
    [Fact]
    public void Validate_Succeeds_ForDefaultOptions()
    {
        var options = new DatabaseResilienceOptions();
        options.Validate(); // Does not throw
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(100)]
    public void Validate_Throws_WhenMaxRetryAttemptsOutOfRange(int maxAttempts)
    {
        var options = new DatabaseResilienceOptions
        {
            MaxRetryAttempts = maxAttempts
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxRetryAttempts must be between 1 and 10", ex.Message);
    }

    [Fact]
    public void Validate_Throws_WhenInitialRetryDelayIsZeroOrNegative()
    {
        var options = new DatabaseResilienceOptions
        {
            InitialRetryDelay = TimeSpan.Zero
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("InitialRetryDelay must be greater than zero", ex.Message);
    }

    [Fact]
    public void Validate_Throws_WhenMaxRetryDelayLessThanInitialRetryDelay()
    {
        var options = new DatabaseResilienceOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(5),
            MaxRetryDelay = TimeSpan.FromSeconds(2)
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxRetryDelay", ex.Message);
        Assert.Contains("must be greater than or equal to InitialRetryDelay", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(700)] // > 10 minutes
    public void Validate_Throws_WhenCommandTimeoutOutOfRange(int seconds)
    {
        var options = new DatabaseResilienceOptions
        {
            CommandTimeout = TimeSpan.FromSeconds(seconds)
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("CommandTimeout must be between 1 second and 10 minutes", ex.Message);
    }
}
