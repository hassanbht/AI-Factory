using AI.Factory.Application.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ShutdownOptionsTests
{
    [Fact]
    public void DefaultOptions_AreValid()
    {
        var options = new ShutdownOptions();
        options.Validate();

        Assert.Equal(30, options.GracefulTimeoutSeconds);
        Assert.Equal(25, options.ForceCancellationAfterSeconds);
    }

    [Fact]
    public void InvalidGracefulTimeout_ThrowsInvalidOperationException()
    {
        var options = new ShutdownOptions { GracefulTimeoutSeconds = 0 };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("GracefulTimeoutSeconds must be positive", ex.Message);
    }

    [Fact]
    public void InvalidForceCancellation_ThrowsInvalidOperationException()
    {
        var options = new ShutdownOptions { ForceCancellationAfterSeconds = -1 };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("ForceCancellationAfterSeconds must be positive", ex.Message);
    }

    [Fact]
    public void ForceCancellationGreaterThanGraceful_ThrowsInvalidOperationException()
    {
        var options = new ShutdownOptions
        {
            GracefulTimeoutSeconds = 10,
            ForceCancellationAfterSeconds = 15
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("cannot be greater than", ex.Message);
    }
}
