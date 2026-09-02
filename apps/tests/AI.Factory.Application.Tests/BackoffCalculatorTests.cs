using AI.Factory.Application.Resilience;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class BackoffCalculatorTests
{
    [Fact]
    public void ConstantBackoff_ReturnsConstantDelay()
    {
        var calculator = new ExponentialBackoffCalculator(() => 0.5);
        var profile = new ResilienceProfile
        {
            BackoffType = BackoffType.Constant,
            InitialDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(10),
            UseJitter = false,
            MaxRetryAttempts = 3
        };

        Assert.Equal(TimeSpan.FromSeconds(2), calculator.CalculateDelay(1, profile));
        Assert.Equal(TimeSpan.FromSeconds(2), calculator.CalculateDelay(2, profile));
        Assert.Equal(TimeSpan.FromSeconds(2), calculator.CalculateDelay(3, profile));
    }

    [Fact]
    public void ExponentialBackoff_GrowsExponentially()
    {
        var calculator = new ExponentialBackoffCalculator(() => 0.5);
        var profile = new ResilienceProfile
        {
            BackoffType = BackoffType.Exponential,
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(20),
            UseJitter = false,
            MaxRetryAttempts = 5
        };

        // attempt 1 -> 1s * 2^0 = 1s
        Assert.Equal(TimeSpan.FromSeconds(1), calculator.CalculateDelay(1, profile));
        // attempt 2 -> 1s * 2^1 = 2s
        Assert.Equal(TimeSpan.FromSeconds(2), calculator.CalculateDelay(2, profile));
        // attempt 3 -> 1s * 2^2 = 4s
        Assert.Equal(TimeSpan.FromSeconds(4), calculator.CalculateDelay(3, profile));
        // attempt 4 -> 1s * 2^3 = 8s
        Assert.Equal(TimeSpan.FromSeconds(8), calculator.CalculateDelay(4, profile));
    }

    [Fact]
    public void ExponentialBackoff_CapsAtMaxDelay()
    {
        var calculator = new ExponentialBackoffCalculator(() => 0.5);
        var profile = new ResilienceProfile
        {
            BackoffType = BackoffType.Exponential,
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(5),
            UseJitter = false,
            MaxRetryAttempts = 5
        };

        // attempt 3 is 4s (under 5s)
        Assert.Equal(TimeSpan.FromSeconds(4), calculator.CalculateDelay(3, profile));
        // attempt 4 is 8s -> capped at 5s
        Assert.Equal(TimeSpan.FromSeconds(5), calculator.CalculateDelay(4, profile));
        // attempt 5 is 16s -> capped at 5s
        Assert.Equal(TimeSpan.FromSeconds(5), calculator.CalculateDelay(5, profile));
    }

    [Fact]
    public void Jitter_CalculatesDeterministicVariations()
    {
        var profile = new ResilienceProfile
        {
            BackoffType = BackoffType.Constant,
            InitialDelay = TimeSpan.FromMilliseconds(1000),
            MaxDelay = TimeSpan.FromMilliseconds(5000),
            UseJitter = true,
            MaxRetryAttempts = 3
        };

        // Midpoint random = 0.5 -> 0% jitter -> exactly 1000ms
        var midpointCalc = new ExponentialBackoffCalculator(() => 0.5);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), midpointCalc.CalculateDelay(1, profile));

        // Upper random = 1.0 -> +20% jitter -> 1200ms
        var upperCalc = new ExponentialBackoffCalculator(() => 1.0);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), upperCalc.CalculateDelay(1, profile));

        // Lower random = 0.0 -> -20% jitter -> 800ms
        var lowerCalc = new ExponentialBackoffCalculator(() => 0.0);
        Assert.Equal(TimeSpan.FromMilliseconds(800), lowerCalc.CalculateDelay(1, profile));
    }

    [Fact]
    public void ZeroAttempts_ReturnsZero()
    {
        var calculator = new ExponentialBackoffCalculator();
        var profile = new ResilienceProfile { MaxRetryAttempts = 0 };

        Assert.Equal(TimeSpan.Zero, calculator.CalculateDelay(1, profile));
        Assert.Equal(TimeSpan.Zero, calculator.CalculateDelay(0, profile));
    }
}
