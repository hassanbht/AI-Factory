using AI.Factory.Api.HealthChecks;
using AI.Factory.Application.Orchestration;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class HealthChecksTests
{
    private static readonly HealthCheckContext Context = new();

    [Fact]
    public async Task DatabaseHealthCheck_ReportsHealthy_WhenDatabaseIsReachable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(connection).Options;
        await using var db = new FactoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var check = new DatabaseHealthCheck(db);
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task DatabaseHealthCheck_ReportsUnhealthy_WhenDatabaseIsUnreachable()
    {
        // A file path under a drive/directory that does not exist — SQLite
        // cannot create the file (no parent directory), so the connection
        // attempt genuinely fails, unlike ":memory:" which always succeeds.
        var options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(@"Data Source=Z:\ai-factory-nonexistent-drive\nonexistent.db")
            .Options;
        await using var db = new FactoryDbContext(options);

        var check = new DatabaseHealthCheck(db);
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task ConfigurationHealthCheck_ReportsHealthy_ForValidOptions()
    {
        var check = new ConfigurationHealthCheck(
            new ExecutionLimitsOptions(),
            new AI.Factory.Application.Configuration.DatabaseOptions { ConnectionString = "Data Source=test.db" },
            new AI.Factory.Application.Configuration.SecretManagementOptions(),
            new AI.Factory.Application.Security.SecurityOptions());
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ConfigurationHealthCheck_ReportsUnhealthy_ForInvalidExecutionLimits()
    {
        var check = new ConfigurationHealthCheck(new ExecutionLimitsOptions { MaxRepairAttempts = 0 });
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task ConfigurationHealthCheck_ReportsUnhealthy_WhenDatabaseConnectionStringEmpty()
    {
        var check = new ConfigurationHealthCheck(
            new ExecutionLimitsOptions(),
            new AI.Factory.Application.Configuration.DatabaseOptions { ConnectionString = "" });
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Database:ConnectionString is required", result.Description);
    }

    [Fact]
    public async Task OpenVikingHealthCheck_ReportsHealthy_WhenDisabled()
    {
        var check = new OpenVikingHealthCheck(new OpenVikingOptions { Enabled = false });
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task OpenVikingHealthCheck_ReportsUnhealthy_WhenEnabledWithoutBaseUrl()
    {
        var check = new OpenVikingHealthCheck(new OpenVikingOptions { Enabled = true, BaseUrl = "" });
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
