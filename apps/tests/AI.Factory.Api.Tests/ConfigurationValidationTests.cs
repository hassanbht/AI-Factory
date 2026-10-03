using AI.Factory.Application.Configuration;
using AI.Factory.Application.Security;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class ConfigurationValidationTests
{
    [Fact]
    public void DatabaseOptions_ValidatesSuccessfully_WhenConnectionStringPresent()
    {
        var options = new DatabaseOptions
        {
            ConnectionString = "Data Source=test.db",
            Provider = "Sqlite"
        };

        options.Validate(); // Does not throw
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DatabaseOptions_Throws_WhenConnectionStringMissing(string? connectionString)
    {
        var options = new DatabaseOptions
        {
            ConnectionString = connectionString!,
            Provider = "Sqlite"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("Database:ConnectionString is required", ex.Message);
        Assert.DoesNotContain("test.db", ex.Message);
    }

    [Fact]
    public void SecretManagementOptions_ValidatesSuccessfully_WhenConfigured()
    {
        var options = new SecretManagementOptions
        {
            Enabled = true,
            Provider = "Environment"
        };

        options.Validate(); // Does not throw
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SecretManagementOptions_Throws_WhenProviderMissingAndEnabled(string? provider)
    {
        var options = new SecretManagementOptions
        {
            Enabled = true,
            Provider = provider!
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("SecretManagement:Provider must be specified", ex.Message);
    }

    [Fact]
    public void AiProviderOptions_ValidatesSuccessfully_WhenApiKeySecretNameProvided()
    {
        var options = new AiProviderOptions
        {
            Providers =
            {
                ["OpenRouter"] = new ProviderCredentialSettings
                {
                    RequiresApiKey = true,
                    ApiKeySecretName = "OPENROUTER_API_KEY"
                }
            }
        };

        options.Validate(); // Does not throw
    }

    [Fact]
    public void AiProviderOptions_Throws_WhenRequiredApiKeySecretNameMissing()
    {
        var options = new AiProviderOptions
        {
            Providers =
            {
                ["OpenRouter"] = new ProviderCredentialSettings
                {
                    RequiresApiKey = true,
                    ApiKeySecretName = "" // Missing!
                }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("requires an API key but ApiKeySecretName is not configured", ex.Message);
    }

    [Fact]
    public void SecurityOptions_Succeeds_WhenAccessKeySecretNameConfigured()
    {
        var options = new SecurityOptions
        {
            OperationalEndpoints = new OperationalEndpointsOptions
            {
                RequireAccessKey = true,
                AccessKey = "", // Empty direct key
                AccessKeySecretName = "OPERATIONAL_ACCESS_KEY" // Secret reference configured
            }
        };

        options.Validate(); // Does not throw
    }

    [Fact]
    public void SecurityOptions_Throws_WhenBothAccessKeyAndSecretNameMissing()
    {
        var options = new SecurityOptions
        {
            OperationalEndpoints = new OperationalEndpointsOptions
            {
                RequireAccessKey = true,
                AccessKey = "",
                AccessKeySecretName = ""
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("neither AccessKey nor AccessKeySecretName is configured", ex.Message);
    }

    [Fact]
    public void ProviderCredentialSettings_DefaultsToOpenAiCompatible()
    {
        var settings = new ProviderCredentialSettings();

        Assert.Equal(AiModelProviderKind.OpenAiCompatible, settings.ProviderType);
    }
}
