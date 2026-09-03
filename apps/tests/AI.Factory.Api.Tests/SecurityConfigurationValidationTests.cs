using AI.Factory.Application.Security;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class SecurityConfigurationValidationTests
{
    [Fact]
    public void Validate_Succeeds_ForDefaultConfiguration()
    {
        var options = new SecurityOptions();
        options.Validate(); // Does not throw
    }

    [Fact]
    public void Validate_Throws_WhenRequireAccessKey_WithEmptyKey()
    {
        var options = new SecurityOptions
        {
            OperationalEndpoints = new OperationalEndpointsOptions
            {
                RequireAccessKey = true,
                AccessKey = ""
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("AccessKey is not configured", ex.Message);
    }

    [Fact]
    public void Validate_Throws_WhenRequireAccessKey_WithShortKey()
    {
        var options = new SecurityOptions
        {
            OperationalEndpoints = new OperationalEndpointsOptions
            {
                RequireAccessKey = true,
                AccessKey = "shortkey" // < 16 chars
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("at least 16 characters", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Validate_Throws_WhenMaxRequestBodySizeBytes_IsInvalid(long limit)
    {
        var options = new SecurityOptions
        {
            RequestLimits = new RequestLimitsOptions
            {
                MaxRequestBodySizeBytes = limit
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxRequestBodySizeBytes must be greater than zero", ex.Message);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(10, 0)]
    [InlineData(10, -5)]
    public void Validate_Throws_WhenRateLimitingPolicy_IsInvalid(int permits, int windowSeconds)
    {
        var options = new SecurityOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                Enabled = true,
                General = new RateLimitPolicyOptions
                {
                    PermitLimit = permits,
                    WindowSeconds = windowSeconds
                }
            }
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Theory]
    [InlineData("not-a-valid-url")]
    [InlineData("ftp//missing-colon")]
    [InlineData("/relative/path")]
    public void Validate_Throws_WhenCorsOrigin_IsInvalid(string invalidOrigin)
    {
        var options = new SecurityOptions
        {
            Cors = new CorsSecurityOptions
            {
                Enabled = true,
                AllowedOrigins = new[] { invalidOrigin }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("invalid absolute URI", ex.Message);
    }

    [Fact]
    public void Validate_Succeeds_WhenCorsOrigins_AreValid()
    {
        var options = new SecurityOptions
        {
            Cors = new CorsSecurityOptions
            {
                Enabled = true,
                AllowedOrigins = new[] { "https://example.com", "http://localhost:3000" }
            }
        };

        options.Validate(); // Does not throw
    }
}
