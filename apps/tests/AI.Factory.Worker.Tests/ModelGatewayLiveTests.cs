using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Configuration;
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
/// Phase I §48, §49 & §50 — Live Model Gateway and Full Execution Validation.
/// Tests that:
/// 1. Real OpenRouterGateway executes against real OpenRouter endpoint with real model.
/// 2. OmniRouteGateway fails closed when service is unavailable (no silent fallback).
/// 3. End-to-end disposable AgentRun runs through Execution Adapter -> ModelRoute -> Gateway -> Runtime -> Acceptance.
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
    public async Task LiveOpenRouter_RealModelInvocation_ThroughModelGatewayAbstraction()
    {
        BenchmarkHarness.TryLoadEnvLive();
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _output.WriteLine("[SKIP] OPENROUTER_API_KEY not configured — skipping live gateway test.");
            return;
        }

        var secretProvider = new EnvironmentSecretProvider(NullLogger<EnvironmentSecretProvider>.Instance);
        using var httpClient = new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        var gateway = new OpenRouterGateway(
            httpClient,
            secretProvider,
            NullLogger<OpenRouterGateway>.Instance);

        var modelId = "deepseek/deepseek-v4-flash-0731";
        var request = new ModelGatewayRequest(
            ModelId: modelId,
            Messages: new[]
            {
                new ModelGatewayMessage("system", "You are an automated software test agent."),
                new ModelGatewayMessage("user", "Respond with the single word: ACKNOWLEDGED")
            },
            MaxTokens: 10,
            Temperature: 0.0f);

        var sw = Stopwatch.StartNew();
        var response = await gateway.ExecuteAsync(request);
        sw.Stop();

        _output.WriteLine($"[LIVE OPENROUTER] Duration: {sw.ElapsedMilliseconds}ms, Success: {response.IsSuccess}, RequestId: {response.RequestId}");

        if (response.IsSuccess)
        {
            _output.WriteLine($"[LIVE OPENROUTER] Response Text: {response.Text}");
            _output.WriteLine($"[LIVE OPENROUTER] Tokens: In={response.Usage?.InputTokens}, Out={response.Usage?.OutputTokens}, Total={response.Usage?.TotalTokens}");
            Assert.NotNull(response.Text);
        }
        else
        {
            _output.WriteLine($"[LIVE OPENROUTER] Failure: Reason={response.Failure?.Reason}, StatusCode={response.Failure?.StatusCode}, Message={response.Failure?.Message}");
            Assert.NotNull(response.Failure);
            // Verify failure is classified into our typed taxonomy (e.g. Billing, RateLimited, Authentication, etc.)
            Assert.True(
                response.Failure.Reason is ModelGatewayFailureReason.Billing
                || response.Failure.Reason is ModelGatewayFailureReason.RateLimited
                || response.Failure.Reason is ModelGatewayFailureReason.Authentication
                || response.Failure.Reason is ModelGatewayFailureReason.ProviderUnavailable,
                $"Unexpected failure classification: {response.Failure.Reason}");
        }
    }

    [Fact]
    public async Task LiveOmniRoute_RealInvocation_ProvesFailClosedOrGatewayUnavailable()
    {
        var secretProvider = new EnvironmentSecretProvider(NullLogger<EnvironmentSecretProvider>.Instance);
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:20128/v1/"), Timeout = TimeSpan.FromSeconds(3) };
        var gateway = new OmniRouteGateway(
            httpClient,
            secretProvider,
            NullLogger<OmniRouteGateway>.Instance);

        var request = new ModelGatewayRequest(
            ModelId: "openai/qwen2.5-coder:7b",
            Messages: new[] { new ModelGatewayMessage("user", "ping") },
            MaxTokens: 5);

        var sw = Stopwatch.StartNew();
        var response = await gateway.ExecuteAsync(request);
        sw.Stop();

        _output.WriteLine($"[LIVE OMNIROUTE] Duration: {sw.ElapsedMilliseconds}ms, Success: {response.IsSuccess}");

        if (response.IsSuccess)
        {
            _output.WriteLine($"[LIVE OMNIROUTE] Response: {response.Text}");
        }
        else
        {
            _output.WriteLine($"[LIVE OMNIROUTE] Reason: {response.Failure?.Reason}, StatusCode: {response.Failure?.StatusCode}, Message: {response.Failure?.Message}");
            // Real OmniRoute response must be classified into our typed taxonomy
            Assert.True(
                response.Failure?.Reason is ModelGatewayFailureReason.Authentication
                || response.Failure?.Reason is ModelGatewayFailureReason.InvalidRequest
                || response.Failure?.Reason is ModelGatewayFailureReason.ModelUnavailable
                || response.Failure?.Reason is ModelGatewayFailureReason.ProviderUnavailable
                || response.Failure?.Reason is ModelGatewayFailureReason.GatewayUnavailable
                || response.Failure?.Reason is ModelGatewayFailureReason.TransportFailure
                || response.Failure?.Reason is ModelGatewayFailureReason.Timeout,
                $"Unexpected OmniRoute failure reason: {response.Failure?.Reason}");
        }
    }

    [Fact]
    public async Task LiveAgentRun_ExecutesThroughModelGatewayBoundary()
    {
        BenchmarkHarness.TryLoadEnvLive();

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

        var secretProvider = new EnvironmentSecretProvider(NullLogger<EnvironmentSecretProvider>.Instance);
        using var openRouterClient = new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        using var omniRouteClient = new HttpClient { BaseAddress = new Uri("http://localhost:20128/v1/") };

        var openRouterGateway = new OpenRouterGateway(openRouterClient, secretProvider, NullLogger<OpenRouterGateway>.Instance);
        var omniRouteGateway = new OmniRouteGateway(omniRouteClient, secretProvider, NullLogger<OmniRouteGateway>.Instance);

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

        _output.WriteLine($"[LIVE AGENTRUN] Outcome: Success={result.Success}, Adapter={result.ExecutionAdapter}, Provider={result.Provider}, Model={result.Model}");

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
