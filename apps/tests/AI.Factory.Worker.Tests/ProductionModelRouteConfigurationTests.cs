using System.Text.Json;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase J (J9/J21/J25) — the shipped Production configuration routes every role to an explicit
/// model through an explicit gateway: no auto-router model ids, no profile without a gateway, and
/// the Phase G/H profile mapping (Implementation/Repair -> OpenHands, Review -> Hermes) unchanged.
/// Read straight from the appsettings files so a config edit cannot bypass it.
/// </summary>
public sealed class ProductionModelRouteConfigurationTests
{
    private static readonly JsonDocumentOptions JsonWithComments = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static TheoryData<string> ProductionFiles => new()
    {
        Path.Combine("src", "AI.Factory.Worker", "appsettings.Production.json"),
        Path.Combine("src", "AI.Factory.Api", "appsettings.Production.json"),
        Path.Combine("src", "AI.Factory.Worker", "appsettings.json"),
        Path.Combine("src", "AI.Factory.Api", "appsettings.json")
    };

    [Theory]
    [MemberData(nameof(ProductionFiles))]
    public void ProductionModelIds_AreExplicit_NeverAnAutoRouter(string relativePath)
    {
        using var document = Load(relativePath);
        var routing = document.RootElement.GetProperty("ModelRouting");

        var modelIds = routing.GetProperty("Models").EnumerateArray()
            .Select(m => m.GetProperty("ModelId").GetString()!)
            .Concat(routing.GetProperty("CategoryProfiles").EnumerateObject()
                .SelectMany(p => p.Value.EnumerateArray().Select(v => v.GetString()!)))
            .ToList();

        Assert.NotEmpty(modelIds);
        Assert.All(modelIds, id =>
        {
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.False(id.StartsWith("openrouter/auto", StringComparison.OrdinalIgnoreCase), id);
            Assert.False(id.StartsWith("auto/", StringComparison.OrdinalIgnoreCase), id);
        });
    }

    [Fact]
    public void ProductionWorkerProfiles_NameAnExplicitEnabledGateway_AndKeepThePhaseHMapping()
    {
        using var document = Load(Path.Combine("src", "AI.Factory.Worker", "appsettings.Production.json"));
        var execution = document.RootElement.GetProperty("Execution");
        var gateways = document.RootElement.GetProperty("ModelGateway").GetProperty("Gateways");

        foreach (var profile in execution.GetProperty("Profiles").EnumerateObject())
        {
            var gateway = profile.Value.GetProperty("Gateway").GetString();
            Assert.False(string.IsNullOrWhiteSpace(gateway), $"Profile '{profile.Name}' has no explicit gateway.");
            Assert.True(gateways.GetProperty(gateway!).GetProperty("Enabled").GetBoolean(),
                $"Profile '{profile.Name}' routes to disabled gateway '{gateway}'.");
        }

        var agentTypes = execution.GetProperty("AgentTypeProfiles");
        Assert.Equal("Hermes", execution.GetProperty("DefaultProfile").GetString());
        Assert.Equal("OpenHands", agentTypes.GetProperty("Implementation").GetString());
        Assert.Equal("OpenHands", agentTypes.GetProperty("Repair").GetString());
        Assert.Equal("Hermes", agentTypes.GetProperty("Review").GetString());
    }

    private static JsonDocument Load(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, relativePath)), JsonWithComments);
    }
}
