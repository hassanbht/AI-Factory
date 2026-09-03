using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AI.Factory.Api.Middleware;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class SensitiveDataProtectionTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public SensitiveDataProtectionTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public void SensitiveDataSanitizer_RedactsBearerTokens()
    {
        var input = "Authorization failed for Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.xyz";
        var sanitized = SensitiveDataSanitizer.Sanitize(input);

        Assert.DoesNotContain("eyJhbGciOi", sanitized);
        Assert.Contains("Bearer [REDACTED]", sanitized);
    }

    [Fact]
    public void SensitiveDataSanitizer_RedactsKeysAndPasswords()
    {
        var input = "apiKey: sk-or-v1-abcdef123456789 and password=SecretPassword123";
        var sanitized = SensitiveDataSanitizer.Sanitize(input);

        Assert.DoesNotContain("sk-or-v1-abcdef123456789", sanitized);
        Assert.DoesNotContain("SecretPassword123", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public void SensitiveDataSanitizer_RedactsLocalFilePaths()
    {
        var windowsInput = @"Failed reading file at C:\AI-Factory\projects\secret\model.json";
        var unixInput = "Failed reading file at /home/user/ai-factory/secrets.key";

        var sanitizedWin = SensitiveDataSanitizer.Sanitize(windowsInput);
        var sanitizedUnix = SensitiveDataSanitizer.Sanitize(unixInput);

        Assert.DoesNotContain(@"C:\AI-Factory", sanitizedWin);
        Assert.Contains("[REDACTED_PATH]", sanitizedWin);

        Assert.DoesNotContain("/home/user", sanitizedUnix);
        Assert.Contains("[REDACTED_PATH]", sanitizedUnix);
    }

    [Fact]
    public void SensitiveDataSanitizer_RedactsMetadataKeys()
    {
        var metadata = new Dictionary<string, string>
        {
            ["model"] = "gpt-4",
            ["api_key"] = "sk-super-secret",
            ["auth_token"] = "secret-token-value",
            ["normal_param"] = "safe value"
        };

        var sanitized = SensitiveDataSanitizer.SanitizeMetadata(metadata);

        Assert.Equal("gpt-4", sanitized["model"]);
        Assert.Equal("[REDACTED]", sanitized["api_key"]);
        Assert.Equal("[REDACTED]", sanitized["auth_token"]);
        Assert.Equal("safe value", sanitized["normal_param"]);
    }

    [Fact]
    public void ApiExceptionMapper_SanitizesLocalPathsInExceptions()
    {
        var mapper = new ApiExceptionMapper();
        var context = new DefaultHttpContext();
        var ex = new ValidationException(@"Invalid path provided: C:\AI-Factory\state\secrets.db");

        var (_, problem) = mapper.CreateProblemDetails(ex, context, "test-correlation");

        Assert.DoesNotContain(@"C:\AI-Factory", problem.Detail);
        Assert.Contains("[REDACTED_PATH]", problem.Detail);
    }

    [Fact]
    public void ApiExceptionMapper_DoesNotLeakDatabaseDetails()
    {
        var mapper = new ApiExceptionMapper();
        var context = new DefaultHttpContext();
        var dbEx = new DbUpdateException("SQLite Error 19: 'UNIQUE constraint failed: projects.Slug'. SQL: INSERT INTO projects ...");

        var (mapping, problem) = mapper.CreateProblemDetails(dbEx, context, "test-correlation");

        Assert.Equal(StatusCodes.Status500InternalServerError, mapping.StatusCode);
        Assert.Equal("A database error occurred.", problem.Detail);
        Assert.DoesNotContain("UNIQUE constraint", problem.Detail);
        Assert.DoesNotContain("INSERT INTO", problem.Detail);
    }

    [Fact]
    public async Task Response_NeverExposesStackTrace_OrAuthorizationHeaders()
    {
        var response = await _client.GetAsync("/api/projects/00000000-0000-0000-0000-000000000001");

        Assert.False(response.Headers.Contains("Authorization"));
        var content = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("StackTrace", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at Program.", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at Microsoft.", content, StringComparison.OrdinalIgnoreCase);
    }
}
