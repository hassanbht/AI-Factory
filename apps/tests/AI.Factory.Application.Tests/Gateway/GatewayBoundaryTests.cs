using System.Net.Http;
using System.Reflection;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Gateway;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Gateways;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

/// <summary>
/// Phase K — the model gateway is a transport/configuration boundary (Architecture A): the
/// runtimes send every model request, the routed gateway is the single authority for which
/// endpoint/credential/provider they are sent to, and Hermes refuses — before launching — any
/// run its own configuration would silently send somewhere else.
/// </summary>
public sealed class GatewayBoundaryTests : IDisposable
{
    private const string KeyValue = "test-openrouter-key-value";

    private readonly string _workspace = Directory.CreateTempSubdirectory("gateway-boundary-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- Hermes route authority

    [Theory]
    [InlineData("OpenRouter")]
    [InlineData(null)] // a profile without a Gateway: the default gateway is checked
    public async Task Hermes_RouteMatchingItsConfiguredTransport_Launches_WithTheKeyOnlyByName(string? gateway)
    {
        var runner = new RecordingRunner();

        var result = await Executor(runner, Resolver()).ExecuteAsync(Request(gateway));

        Assert.True(result.Success);
        var run = runner.Requests[0];
        Assert.Equal("run", run.Arguments[0]);
        Assert.Equal("openrouter", run.Arguments[run.Arguments.ToList().IndexOf("--provider") + 1]);
        Assert.DoesNotContain(run.Arguments, a => a.Contains(KeyValue, StringComparison.Ordinal));
        Assert.Contains("OPENROUTER_API_KEY", run.Arguments);
        Assert.Equal(KeyValue, run.EnvironmentVariables!["OPENROUTER_API_KEY"]);
    }

    [Theory]
    [InlineData("provider", "the gateway needs 'openrouter'")]
    [InlineData("noprovider", "Hermes:Provider must be set explicitly")]
    [InlineData("secret", "is not the gateway's 'OPENROUTER_API_KEY'")]
    [InlineData("envvar", "the provider reads 'OPENROUTER_API_KEY'")]
    [InlineData("baseurl", "Hermes would call 'http://localhost:20128/v1' (OPENROUTER_BASE_URL)")]
    public async Task Hermes_ConfigurationThatWouldSilentlyBypassTheRoute_IsRefused_BeforeLaunch(string conflict, string reason)
    {
        var runner = new RecordingRunner();
        var executor = Executor(runner, Resolver(), o =>
        {
            switch (conflict)
            {
                case "provider": o.Provider = "openai"; break;
                case "noprovider": o.Provider = null; break;
                case "secret": o.ProviderApiKeySecretName = "OMNIROUTE_API_KEY"; break;
                case "envvar": o.ProviderApiKeyEnvironmentVariable = "OPENAI_API_KEY"; break;
                case "baseurl": o.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = "http://localhost:20128/v1"; break;
            }
        });

        var result = await executor.ExecuteAsync(Request("OpenRouter"));

        Assert.False(result.Success);
        Assert.StartsWith("Model gateway route rejected: Hermes cannot honour the routed gateway 'OpenRouter'", result.Error);
        Assert.Contains(reason, result.Error);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Hermes_GatewayEndpointThatHermesWouldNotCallByDefault_RequiresAnExplicitOverride()
    {
        // OpenRouter gateway configured behind a proxy, Hermes left on its built-in endpoint.
        var proxied = Executor(new RecordingRunner(), Resolver(openRouterBaseUrl: "https://proxy.internal/api/v1"));
        var refused = await proxied.ExecuteAsync(Request("OpenRouter"));
        Assert.Contains("Hermes would call 'https://openrouter.ai/api/v1'", refused.Error);

        // OmniRoute via Hermes' `openai` provider: no verified built-in endpoint, so the override is mandatory.
        var omniRoute = Executor(new RecordingRunner(), Resolver(), o =>
        {
            o.Provider = "openai";
            o.ProviderApiKeySecretName = "OMNIROUTE_API_KEY";
            o.ProviderApiKeyEnvironmentVariable = "OPENAI_API_KEY";
        });
        var noOverride = await omniRoute.ExecuteAsync(Request("OmniRoute"));
        Assert.Contains("no verified built-in endpoint for provider 'openai'; set OPENAI_BASE_URL", noOverride.Error);

        var runner = new RecordingRunner();
        var explicitOverride = Executor(runner, Resolver(), o =>
        {
            o.Provider = "openai";
            o.ProviderApiKeySecretName = "OMNIROUTE_API_KEY";
            o.ProviderApiKeyEnvironmentVariable = "OPENAI_API_KEY";
            o.AdditionalEnvironmentVariables["OPENAI_BASE_URL"] = OmniRouteGateway.DefaultBaseUrl;
        });
        Assert.True((await explicitOverride.ExecuteAsync(Request("OmniRoute"))).Success);
        Assert.NotEmpty(runner.Requests);
    }

    [Fact]
    public async Task Hermes_BaseUrlOverrideEqualToTheGatewayEndpoint_IsAccepted()
    {
        var runner = new RecordingRunner();
        var executor = Executor(runner, Resolver(), o => o.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = "https://openrouter.ai/api/v1/");

        var result = await executor.ExecuteAsync(Request("OpenRouter"));

        Assert.True(result.Success);
        Assert.NotEmpty(runner.Requests);
    }

    [Fact]
    public async Task Hermes_UnroutedCall_WhenTheDefaultGatewayIsDisabled_IsRefused_NeverRunsAnyway()
    {
        var runner = new RecordingRunner();

        var result = await Executor(runner, Resolver(openRouterEnabled: false)).ExecuteAsync(Request(gateway: null));

        Assert.False(result.Success);
        Assert.Contains("is disabled", result.Error);
        Assert.Empty(runner.Requests);
    }

    // ---------------------------------------------------------------- contract and layering

    [Fact]
    public void IModelGateway_DescribesTransport_AndOwnsNoModelExecution()
    {
        var members = typeof(IModelGateway).GetMembers().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "FormatModelIdentifier", "GatewayKey", "GetTransportInfo", "get_GatewayKey" }, members);
        Assert.DoesNotContain(typeof(IModelGateway).GetMethods(), m => typeof(Task).IsAssignableFrom(m.ReturnType));
    }

    [Theory]
    [InlineData(typeof(OpenRouterGateway))]
    [InlineData(typeof(OmniRouteGateway))]
    public void GatewayImplementations_HoldConfigurationOnly_NoHttpClientNoSecretAccess(Type gateway)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        Assert.All(gateway.GetConstructors().SelectMany(c => c.GetParameters()), p => Assert.Equal(typeof(string), p.ParameterType));
        Assert.All(gateway.GetFields(all), f => Assert.Equal(typeof(string), f.FieldType));
    }

    [Fact]
    public void ApplicationLayer_DependsOnNoHttpTransportType()
    {
        var forbidden = new[] { typeof(HttpClient), typeof(HttpRequestMessage), typeof(HttpResponseMessage), typeof(HttpMessageHandler) };
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in typeof(IModelGateway).Assembly.GetTypes())
        {
            var referenced = type.GetFields(all).Select(f => f.FieldType)
                .Concat(type.GetProperties(all).Select(p => p.PropertyType))
                .Concat(type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));

            Assert.All(referenced, t => Assert.DoesNotContain(t, forbidden));
        }
    }

    [Fact]
    public void NoProductionCode_SendsAModelCompletion_OrHardcodesAGatewayModelPrefix_OutsideTheGatewayLayer()
    {
        var src = Path.Combine(RepositoryAppsRoot(), "src");
        var gatewayLayer = Path.Combine(src, "AI.Factory.Infrastructure", "Gateways") + Path.DirectorySeparatorChar;

        var sources = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(sources);
        Assert.All(sources, f => Assert.DoesNotContain("chat/completions", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase));
        Assert.All(sources.Where(f => !f.StartsWith(gatewayLayer, StringComparison.OrdinalIgnoreCase)),
            f => Assert.DoesNotContain("\"openrouter/", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- helpers

    private static string RepositoryAppsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static ModelGatewayResolver Resolver(bool openRouterEnabled = true, string? openRouterBaseUrl = null) => new(
        new ModelGatewayRegistry(new IModelGateway[] { new OpenRouterGateway(openRouterBaseUrl), new OmniRouteGateway() }),
        new ModelGatewayOptions
        {
            DefaultGateway = OpenRouterGateway.Key,
            Gateways =
            {
                [OpenRouterGateway.Key] = new GatewayEntryOptions { BaseUrl = openRouterBaseUrl ?? OpenRouterGateway.DefaultBaseUrl, Enabled = openRouterEnabled },
                [OmniRouteGateway.Key] = new GatewayEntryOptions { BaseUrl = OmniRouteGateway.DefaultBaseUrl }
            }
        });

    private HermesAgentExecutor Executor(RecordingRunner runner, ModelGatewayResolver resolver, Action<HermesOptions>? configure = null)
    {
        var options = new HermesOptions
        {
            DockerImage = "test/hermes:pinned",
            NetworkMode = "bridge",
            Provider = "openrouter",
            ProviderApiKeySecretName = "OPENROUTER_API_KEY",
            ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
            TimeoutSeconds = 60,
            WorkspaceRoot = Path.Combine(_workspace, "scratch")
        };
        configure?.Invoke(options);

        return new HermesAgentExecutor(
            options,
            runner,
            NullLogger<HermesAgentExecutor>.Instance,
            secretProvider: new FakeSecretProvider(new Dictionary<string, string?> { ["OPENROUTER_API_KEY"] = KeyValue, ["OMNIROUTE_API_KEY"] = KeyValue }),
            gatewayResolver: resolver);
    }

    private AgentExecutionRequest Request(string? gateway) =>
        new("coder", "deepseek/deepseek-v4-flash-0731", "prompt", _workspace, AgentRunId: Guid.NewGuid(), Gateway: gateway);

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = new();

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Arguments.FirstOrDefault() == "run")
            {
                Requests.Add(request);
                HermesRunReportFake.Write(request);
            }

            return Task.FromResult(new ProcessRunResult(0, "done", "", TimedOut: false));
        }
    }
}
