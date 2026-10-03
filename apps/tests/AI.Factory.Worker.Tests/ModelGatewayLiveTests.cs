using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Gateways;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Worker.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase I §50 — a disposable AgentRun routes Execution Profile -> ModelRoute -> dispatcher to the
/// selected adapter. The adapter is a synthetic in-test fake that returns Succeeded without any
/// gateway or model call, so this proves routing only — never model generation (Phase J). The
/// Phase I direct-gateway live calls were removed in Phase K together with
/// IModelGateway.ExecuteAsync, which no production code ever called; real gateway traffic is
/// exercised only through the runtimes, in GatewayLiveAgentRunTests.
/// </summary>
public sealed class ModelGatewayLiveTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempRoot;
    private readonly SqliteConnection _connection;

    public ModelGatewayLiveTests(ITestOutputHelper output)
    {
        _output = output;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"aif-live-gateway-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(_tempRoot);

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch
        {
            // Best effort
        }
    }

    [Fact]
    public async Task DisposableAgentRun_RoutesProfileAndModelRoute_ToASyntheticAdapter_NoModelCall()
    {
        var dbOptions = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = new FactoryDbContext(dbOptions);
        db.Database.EnsureCreated();

        var projectId = Guid.NewGuid();
        var project = new Project
        {
            Id = projectId,
            Name = "LiveGatewayProject",
            Slug = "live-gateway-project",
            Description = "Test project for unified gateway boundary",
            Status = ProjectStatus.Created,
            RootPath = _tempRoot
        };
        db.Projects.Add(project);

        var workItemId = Guid.NewGuid();
        var workItem = new WorkItem
        {
            Id = workItemId,
            ProjectId = projectId,
            Title = "Gateway Verification Task",
            Description = "Verify gateway resolution",
            Status = "Ready"
        };
        db.WorkItems.Add(workItem);

        var agentDefId = Guid.NewGuid();
        var agentDef = new AgentDefinition
        {
            Id = agentDefId,
            Name = "GatewayVerifierAgent",
            AgentType = "Implementation",
            SystemPrompt = "You are a test verification agent.",
            Enabled = true
        };
        db.AgentDefinitions.Add(agentDef);

        var agentRunId = Guid.NewGuid();
        var agentRun = new AgentRun
        {
            Id = agentRunId,
            CorrelationId = Guid.NewGuid(),
            ProjectId = projectId,
            WorkItemId = workItemId,
            AgentDefinitionId = agentDefId,
            AgentDefinition = agentDef,
            Status = AgentRunStatus.Running,
            Model = "deepseek/deepseek-v4-flash-0731",
            Provider = "OpenRouter",
            WorkspacePath = _tempRoot,
            Attempt = 1,
            Iteration = 1
        };
        db.AgentRuns.Add(agentRun);
        await db.SaveChangesAsync();

        // Configure Gateway & Execution Options
        var modelGatewayOptions = new ModelGatewayOptions
        {
            DefaultGateway = "OpenRouter",
            Gateways =
            {
                ["OpenRouter"] = new() { Enabled = true },
                ["OmniRoute"] = new() { Enabled = false }
            }
        };

        var openRouterGateway = new OpenRouterGateway();
        var omniRouteGateway = new OmniRouteGateway();

        var gatewayRegistry = new ModelGatewayRegistry(new IModelGateway[] { openRouterGateway, omniRouteGateway });
        var gatewayResolver = new ModelGatewayResolver(gatewayRegistry, modelGatewayOptions);

        var executionOptions = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles =
            {
                ["Hermes"] = new() { Adapter = "Hermes", Gateway = "OpenRouter" },
                ["OpenHands"] = new() { Adapter = "OpenHands", Gateway = "OpenRouter" }
            },
            AgentTypeProfiles =
            {
                ["Implementation"] = "OpenHands",
                ["Repair"] = "OpenHands",
                ["Review"] = "Hermes"
            }
        };
        executionOptions.Validate();

        var profileResolver = new ExecutionProfileResolver(
            executionOptions,
            new FakeAdapterRegistry(new[]
            {
                new FakeExecutionAdapter("Hermes"),
                new FakeExecutionAdapter("OpenHands")
            }));

        var dispatcher = new AdapterDispatchingAgentRuntime(
            profileResolver,
            new FailureClassifier(),
            NullLogger<AdapterDispatchingAgentRuntime>.Instance);

        // Execute through dispatcher
        var result = await dispatcher.ExecuteAsync(agentRun);

        _output.WriteLine($"[SYNTHETIC ADAPTER — NO MODEL CALL] Outcome: Success={result.Success}, Adapter={result.ExecutionAdapter}, Provider={result.Provider}, Model={result.Model}");

        Assert.Equal("OpenHands", result.ExecutionAdapter);
        Assert.Equal("OpenRouter", result.Provider);
        Assert.Equal("deepseek/deepseek-v4-flash-0731", result.Model);
        Assert.Equal("fake output", result.Output);
    }

    private sealed class FakeAdapterRegistry : IExecutionAdapterRegistry
    {
        private readonly Dictionary<string, IExecutionAdapter> _adapters;

        public FakeAdapterRegistry(IEnumerable<IExecutionAdapter> adapters)
        {
            _adapters = adapters.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<string> Keys => _adapters.Keys.ToList();

        public bool TryGet(string key, out IExecutionAdapter? adapter) => _adapters.TryGetValue(key, out adapter);
    }

    private sealed class FakeExecutionAdapter : IExecutionAdapter
    {
        public FakeExecutionAdapter(string key)
        {
            Key = key;
        }

        public string Key { get; }

        public ExecutionCapabilities Capabilities => ExecutionCapabilities.FactoryRoutedModel;

        public Task<ExecutionOutcome> ExecuteAsync(AgentExecutionSpec spec, CancellationToken cancellationToken = default)
        {
            var outcome = new ExecutionOutcome(
                ExecutionOutcomeStatus.Succeeded,
                Output: "fake output",
                ErrorMessage: null,
                FailureCategory: null,
                DurationMs: 42,
                Usage: new ExecutionUsage(10, 5, 15),
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                Model: spec.Model,
                ExitReason: "exit:0");

            return Task.FromResult(outcome);
        }
    }
}
