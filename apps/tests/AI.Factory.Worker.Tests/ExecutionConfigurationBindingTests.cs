using AI.Factory.Application.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Execution-adapter Phase B — the "Execution" configuration section as the real
/// Worker composition root (<c>Program.ConfigureServices</c>) binds, defaults and
/// validates it. Only the registered ExecutionOptions instance is inspected; the
/// host is never built or run.
/// </summary>
public sealed class ExecutionConfigurationBindingTests
{
    private static ExecutionOptions ConfigureWith(Dictionary<string, string?> overrides, bool isolated = true)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = FindWorkerProjectDirectory(),
            EnvironmentName = "Production"
        });

        if (isolated)
        {
            ((IConfigurationBuilder)builder.Configuration).Sources.Clear();
        }

        builder.Configuration.AddInMemoryCollection(overrides);

        Program.ConfigureServices(builder);

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(ExecutionOptions));
        return Assert.IsType<ExecutionOptions>(descriptor.ImplementationInstance);
    }

    [Fact]
    public void ProductionConfigurationFiles_BindAndValidateApprovedProfiles()
    {
        var options = ConfigureWith(new Dictionary<string, string?>(), isolated: false);

        Assert.Equal("Hermes", options.DefaultProfile);
        Assert.Equal("Hermes", options.Profiles["Hermes"].Adapter);
        Assert.Equal("OpenHands", options.Profiles["OpenHands"].Adapter);
        Assert.Equal("OpenHands", options.Profiles["FastCoding"].Adapter);
        Assert.Equal("OpenHands", options.AgentTypeProfiles["Implementation"]);
        Assert.Equal("OpenHands", options.AgentTypeProfiles["Repair"]);
        Assert.Equal("Hermes", options.AgentTypeProfiles["Review"]);
    }

    [Fact]
    public void WorkerAndApi_ProductionConfigurations_AreIdenticalAndConflictFree()
    {
        var workerDir = FindWorkerProjectDirectory();
        var apiDir = Path.Combine(Directory.GetParent(workerDir)!.FullName, "AI.Factory.Api");

        var workerConfig = new ConfigurationBuilder()
            .SetBasePath(workerDir)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var apiConfig = new ConfigurationBuilder()
            .SetBasePath(apiDir)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var workerOptions = new ExecutionOptions();
        workerConfig.GetSection(ExecutionOptions.SectionName).Bind(workerOptions);
        workerOptions.Validate();

        var apiOptions = new ExecutionOptions();
        apiConfig.GetSection(ExecutionOptions.SectionName).Bind(apiOptions);
        apiOptions.Validate();

        Assert.Equal(workerOptions.DefaultProfile, apiOptions.DefaultProfile);
        Assert.Equal(workerOptions.Profiles.Keys.OrderBy(k => k), apiOptions.Profiles.Keys.OrderBy(k => k));
        foreach (var key in workerOptions.Profiles.Keys)
        {
            Assert.Equal(workerOptions.Profiles[key].Adapter, apiOptions.Profiles[key].Adapter);
        }
        Assert.Equal(workerOptions.AgentTypeProfiles.Keys.OrderBy(k => k), apiOptions.AgentTypeProfiles.Keys.OrderBy(k => k));
        foreach (var key in workerOptions.AgentTypeProfiles.Keys)
        {
            Assert.Equal(workerOptions.AgentTypeProfiles[key], apiOptions.AgentTypeProfiles[key]);
        }
    }

    [Fact]
    public void ExplicitProfiles_BindCaseInsensitively_AndResolveThroughAgentTypeMapping()
    {
        var options = ConfigureWith(new Dictionary<string, string?>
        {
            ["Execution:DefaultProfile"] = "Standard",
            ["Execution:Profiles:Standard:Adapter"] = "Hermes",
            ["Execution:Profiles:RepairProfile:Adapter"] = "Hermes",
            ["Execution:AgentTypeProfiles:Repair"] = "RepairProfile",
            ["Execution:Adapters:Hermes:Enabled"] = "true"
        });

        Assert.Equal("Standard", options.DefaultProfile);
        Assert.Equal("RepairProfile", options.AgentTypeProfiles["repair"]);
        Assert.Equal("Hermes", options.Profiles["standard"].Adapter);
        Assert.True(options.Adapters["hermes"].Enabled);
        // No built-in profile was added on top of an explicit section.
        Assert.Equal(2, options.Profiles.Count);
    }

    [Fact]
    public void ExplicitlyDisabledAdapter_Binds()
    {
        var options = ConfigureWith(new Dictionary<string, string?> { ["Execution:Adapters:Hermes:Enabled"] = "false" });

        Assert.False(options.Adapters["Hermes"].Enabled);
    }

    [Theory]
    [InlineData("Execution:DefaultProfile", "Missing")]
    [InlineData("Execution:Profiles:Custom:Adapter", "Hermes")]
    public void IncompleteOrInconsistentSection_FailsAtStartup_InsteadOfDefaultingSilently(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigureWith(new Dictionary<string, string?> { [key] = value }));

        Assert.Contains(nameof(ExecutionOptions), ex.Message);
    }

    [Fact]
    public void AgentTypeMappedToUnknownProfile_FailsAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigureWith(new Dictionary<string, string?>
        {
            ["Execution:AgentTypeProfiles:Implementation"] = "Nope"
        }));

        Assert.Contains("'Nope'", ex.Message);
    }

    private static string FindWorkerProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate AI.Factory.slnx above the test assembly.");
        }

        return Path.Combine(dir.FullName, "src", "AI.Factory.Worker");
    }
}
