using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.OpenHands;
using AI.Factory.Infrastructure.Gateways;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

/// <summary>
/// Phase J (J4/J5/J20) — deterministic end-to-end proof that a model-gateway failure never
/// becomes a successful AgentRun:
///   gateway failure -> runtime -> execution adapter -> ExecutionOutcome ->
///   AdapterDispatchingAgentRuntime -> AgentOrchestrator -> persisted AgentRun ->
///   AgentRunWorkItemSync (acceptance boundary).
/// No real gateway is contacted. The Hermes process is scripted exactly as Hermes 0.20.6
/// behaved live against OpenRouter HTTP 402: the gateway error printed as the final
/// response on stdout, exit code 0, and a run report with "failed": true. The real
/// OpenRouter/OmniRoute gateway descriptors are registered; since Phase K they own no HTTP
/// at all, so the runtime is the only thing that could ever send a model request.
/// </summary>
public sealed class GatewayFailureAgentRunEndToEndTests : IDisposable
{
    private const string Model = "deepseek/deepseek-v4-flash-0731";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly string _workspace;

    public GatewayFailureAgentRunEndToEndTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using (var db = new FactoryDbContext(_options))
        {
            db.Database.EnsureCreated();
        }

        _workspace = Directory.CreateTempSubdirectory("gateway-e2e-").FullName;
        File.WriteAllText(Path.Combine(_workspace, "README.md"), "baseline");
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- Hermes

    [Theory]
    [InlineData(402, "Billing", "Billing or credits exhausted: HTTP 402: Insufficient credits. This account never purchased credits.")]
    [InlineData(401, "Authentication", "HTTP 401: No auth credentials found")]
    [InlineData(403, "Authorization", "HTTP 403: Key is not allowed to access this model")]
    [InlineData(429, "RateLimited", "Rate limited after 3 retries — HTTP 429: Too many requests")]
    [InlineData(404, "ModelUnavailable", "HTTP 404: No endpoints found for this model")]
    [InlineData(503, "GatewayUnavailable", "API failed after 3 retries — HTTP 503: Service unavailable")]
    public async Task HermesReportedGatewayFailure_WithExitCodeZero_PersistsFailedAgentRun_AndNoAcceptance(
        int status, string expectedReason, string hermesStdout)
    {
        var (runId, workItemId) = await SeedAsync();
        // Zero tokens in the report: the run never reached the model.
        var process = new ScriptedHermesProcess(exitCode: 0, hermesStdout, reportFailed: true, inputTokens: 0, outputTokens: 0, totalTokens: 0);

        var result = await OrchestrateAsync(runId, HermesRuntime(process, "OpenRouter", hermesProvider: "openrouter"));

        Assert.False(result.Success);
        Assert.Equal(AgentRunStatus.Failed, result.FinalStatus);

        var (run, artifacts, structured) = await LoadAsync(runId);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(FailureCategory.Agent, run.FailureCategory);
        Assert.Contains($"HTTP {status}", run.ErrorMessage);
        Assert.StartsWith("Hermes reported a failed run (exit code 0)", run.ErrorMessage);

        // No success metadata, no usage fabricated from a run that produced none.
        Assert.Null(run.PromptTokens);
        Assert.Null(run.CompletionTokens);
        Assert.Null(run.TotalTokens);
        Assert.Empty(artifacts);
        Assert.Equal(StructuredResultStatus.Failed, structured.Status);
        Assert.Equal(expectedReason, structured.Metadata!["GatewayFailure"]);
        Assert.Equal("OpenRouter", structured.Metadata["Gateway"]);
        Assert.Equal("Hermes", structured.Metadata["ExecutionAdapter"]);
        Assert.Equal("exit:0", structured.Metadata["ExitReason"]);

        // Exactly one Hermes launch, and the run report was consumed, not left in the workspace.
        Assert.Single(process.Runs);
        Assert.False(File.Exists(Path.Combine(_workspace, HermesAgentExecutor.RunReportFileName(runId))));

        // Acceptance boundary: no verification, no completion, WorkItem Failed.
        var (workItem, gate, completion) = await SyncAsync(runId);
        Assert.Equal(workItemId, workItem.Id);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem.Status);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, completion.Calls);
    }

    [Fact]
    public async Task OmniRoute401_ThroughHermes_StaysOnOmniRoute_AndFailsWithAuthentication()
    {
        var (runId, _) = await SeedAsync();
        var process = new ScriptedHermesProcess(
            exitCode: 0, "HTTP 401: No active credentials for provider: openai.", reportFailed: true);

        var result = await OrchestrateAsync(runId, HermesRuntime(process, "OmniRoute", hermesProvider: "openai"));

        Assert.Equal(AgentRunStatus.Failed, result.FinalStatus);
        var (run, _, structured) = await LoadAsync(runId);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal("Authentication", structured.Metadata!["GatewayFailure"]);
        Assert.Equal("OmniRoute", structured.Metadata["Gateway"]);

        // No silent OpenRouter fallback: one launch, on the OmniRoute provider only.
        var launch = Assert.Single(process.Runs);
        var args = launch.Arguments.ToList();
        Assert.Equal("openai", args[args.IndexOf("--provider") + 1]);
        Assert.DoesNotContain("openrouter", args);

        var (workItem, gate, _) = await SyncAsync(runId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem.Status);
        Assert.Equal(0, gate.Calls);
    }

    [Fact]
    public async Task HermesGatewayFailureMessage_IsSanitizedBeforePersistence()
    {
        var (runId, _) = await SeedAsync();
        var process = new ScriptedHermesProcess(
            exitCode: 0,
            "HTTP 401: Incorrect API key provided: sk-or-v1-0123456789abcdef0123456789abcdef",
            reportFailed: true);

        await OrchestrateAsync(runId, HermesRuntime(process, "OpenRouter", hermesProvider: "openrouter"));

        var (run, _, structured) = await LoadAsync(runId);
        Assert.DoesNotContain("sk-or-v1-0123456789abcdef", run.ErrorMessage);
        Assert.DoesNotContain("sk-or-v1-0123456789abcdef", run.StructuredResult);
        Assert.Equal("Authentication", structured.Metadata!["GatewayFailure"]);
    }

    [Fact]
    public async Task HermesSuccess_WithRunReport_PersistsMeasuredUsage_AndOnlyThenReachesVerification()
    {
        var (runId, _) = await SeedAsync();
        var process = new ScriptedHermesProcess(
            exitCode: 0, "ACK", reportFailed: false, inputTokens: 120, outputTokens: 30, totalTokens: 150);

        var result = await OrchestrateAsync(runId, HermesRuntime(process, "OpenRouter", hermesProvider: "openrouter"));

        Assert.True(result.Success);
        var (run, _, structured) = await LoadAsync(runId);
        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.Equal(120, run.PromptTokens);
        Assert.Equal(30, run.CompletionTokens);
        Assert.Equal(150, run.TotalTokens);
        Assert.Equal("OpenRouter", structured.Metadata!["Gateway"]);
        Assert.False(structured.Metadata.ContainsKey("GatewayFailure"));

        // The contrast with the failure path: a real success is handed to verification.
        var (_, gate, _) = await SyncAsync(runId);
        Assert.Equal(1, gate.Calls);
    }

    // Phase L — exit 0 with no proof of completion is not a successful implementation.
    [Theory]
    [InlineData(null, null, HermesAgentExecutor.ReportUnavailableError)]   // report missing
    [InlineData(false, false, HermesAgentExecutor.RunIncompleteError)]     // "failed": false, "completed": false
    public async Task HermesExitZero_WithoutACompletedRunReport_PersistsFailedAgentRun_AndNoAcceptance(
        bool? reportFailed, bool? reportCompleted, string expectedError)
    {
        var (runId, workItemId) = await SeedAsync();
        // The agent's own prose mentions an HTTP status: on an unproven run that is not a gateway failure.
        var process = new ScriptedHermesProcess(
            exitCode: 0, "I think I am done; the API I called returned HTTP 429 once.", reportFailed, reportCompleted: reportCompleted);

        var result = await OrchestrateAsync(runId, HermesRuntime(process, "OpenRouter", hermesProvider: "openrouter"));

        Assert.False(result.Success);
        var (run, artifacts, structured) = await LoadAsync(runId);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(FailureCategory.Agent, run.FailureCategory);
        Assert.Equal(expectedError, run.ErrorMessage);
        Assert.Empty(artifacts);
        Assert.Equal(StructuredResultStatus.Failed, structured.Status);
        Assert.False(structured.Metadata!.ContainsKey("GatewayFailure"));
        Assert.Single(process.Runs);   // not retried
        Assert.Empty(Directory.GetFiles(_workspace, ".aif-hermes-run-*"));

        var (workItem, gate, completion) = await SyncAsync(runId);
        Assert.Equal(workItemId, workItem.Id);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem.Status);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, completion.Calls);
    }

    [Fact]
    public async Task RouteToGatewayHermesIsNotConfiguredFor_FailsClosed_WithoutLaunchingHermes()
    {
        var (runId, _) = await SeedAsync();
        var process = new ScriptedHermesProcess(exitCode: 0, "ACK", reportFailed: false);

        // Profile routes to OmniRoute; Hermes is configured for openrouter only.
        await OrchestrateAsync(runId, HermesRuntime(process, "OmniRoute", hermesProvider: "openrouter"));

        var (run, _, structured) = await LoadAsync(runId);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains("cannot honour the routed gateway 'OmniRoute'", run.ErrorMessage);
        Assert.Empty(process.Runs);
        Assert.False(structured.Metadata!.ContainsKey("GatewayFailure"));
    }

    [Theory]
    [InlineData("OmniRoute", "is disabled")]
    [InlineData("NoSuchGateway", "is not registered")]
    public async Task RouteToDisabledOrUnknownGateway_FailsClosed_WithoutLaunchingHermes(string gateway, string expected)
    {
        var (runId, _) = await SeedAsync();
        var process = new ScriptedHermesProcess(exitCode: 0, "ACK", reportFailed: false);

        await OrchestrateAsync(runId, HermesRuntime(process, gateway, hermesProvider: "openai", omniRouteEnabled: false));

        var (run, _, _) = await LoadAsync(runId);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains(expected, run.ErrorMessage);
        Assert.Empty(process.Runs);
    }

    // ---------------------------------------------------------------- Review (Phase H path)

    [Fact]
    public async Task Review_OnHermesGateway402_BlocksCompletion_InsteadOfPassingOnTheBillingText()
    {
        // Before Phase J this outcome was Succeeded, the billing text failed to parse as
        // findings, degraded to a single Info finding, and the review did NOT block.
        var process = new ScriptedHermesProcess(
            exitCode: 0, "Billing or credits exhausted: HTTP 402: Insufficient credits.", reportFailed: true);
        var dispatcher = (IAgentExecutionDispatcher)HermesRuntime(process, "OpenRouter", hermesProvider: "openrouter");

        var routing = new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new()
                {
                    ModelId = "openai/gpt-5.1", Provider = "openrouter", ReasoningStrength = 5,
                    MaxContextTokens = 128_000, CostTier = 3, IsAvailable = true
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["code-review"] = new List<string> { "openai/gpt-5.1" }
            }
        };
        var reviewer = new AgentCodeReviewer(dispatcher, new ModelRouter(routing), NullLogger<AgentCodeReviewer>.Instance);

        var review = await reviewer.ReviewAsync(new ReviewContext(
            WorkItemId: Guid.NewGuid(), ProjectId: Guid.NewGuid(), RequirementsText: "Req",
            AcceptanceCriteria: null, ArchitectureText: "Arch", ChangedFiles: new[] { "src/A.cs" },
            TestOutput: "ok", TestsSucceeded: true, BuildOutput: "ok", BuildSucceeded: true, TaskHistoryText: ""));

        Assert.True(review.BlocksCompletion);
        var finding = Assert.Single(review.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, finding.Severity);
        Assert.Contains("Independent review could not be performed", finding.Description);
        Assert.Single(process.Runs);
    }

    // ---------------------------------------------------------------- OpenHands

    [Fact]
    public async Task OpenRouter402_ThroughOpenHands_ConversationError_PersistsFailedAgentRun_AndNoAcceptance()
    {
        var (runId, _) = await SeedAsync();
        var client = new ScriptedOpenHandsClient("running", "error");

        var result = await OrchestrateAsync(runId, OpenHandsRuntime(client));

        Assert.False(result.Success);
        var (run, artifacts, structured) = await LoadAsync(runId);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains("error state", run.ErrorMessage);
        Assert.Null(run.TotalTokens);
        Assert.Empty(artifacts);
        Assert.Equal("OpenHands", structured.Metadata!["ExecutionAdapter"]);
        Assert.Equal("OpenRouter", structured.Metadata["Gateway"]);
        // OpenHands reports no HTTP status for the failure: the gateway reason is not
        // available and is not guessed.
        Assert.False(structured.Metadata.ContainsKey("GatewayFailure"));

        // The model route reached OpenHands through the gateway transport, not a direct call.
        var llm = client.CreateRequest!.AgentSettings!.Llm!;
        Assert.Equal($"openrouter/{Model}", llm.Model);
        Assert.Equal(OpenRouterGateway.DefaultBaseUrl, llm.BaseUrl);

        var (workItem, gate, completion) = await SyncAsync(runId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem.Status);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, completion.Calls);
    }

    [Fact]
    public async Task OpenHandsSuccess_WithPartialUsage_NeverZeroFillsTheUnknownCount()
    {
        var (runId, _) = await SeedAsync();
        var client = new ScriptedOpenHandsClient("running", "finished")
        {
            Usage = new OpenHandsTokenUsageDto { PromptTokens = 42, CompletionTokens = null }
        };

        await OrchestrateAsync(runId, OpenHandsRuntime(client));

        var (run, _, _) = await LoadAsync(runId);
        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.Equal(42, run.PromptTokens);
        Assert.Null(run.CompletionTokens);
        Assert.Null(run.TotalTokens);
    }

    // ---------------------------------------------------------------- pipeline

    private ModelGatewayResolver Resolver(bool omniRouteEnabled, ExecutionOptions execution)
    {
        // Phase K — the real gateway descriptors; they own no HTTP, so no gateway call can happen.
        var registry = new ModelGatewayRegistry();
        registry.Register(new OpenRouterGateway());
        registry.Register(new OmniRouteGateway());

        var options = new ModelGatewayOptions
        {
            DefaultGateway = OpenRouterGateway.Key,
            Gateways =
            {
                [OpenRouterGateway.Key] = new GatewayEntryOptions { BaseUrl = OpenRouterGateway.DefaultBaseUrl },
                [OmniRouteGateway.Key] = new GatewayEntryOptions
                {
                    BaseUrl = OmniRouteGateway.DefaultBaseUrl, Enabled = omniRouteEnabled
                }
            }
        };

        return new ModelGatewayResolver(registry, options, execution);
    }

    private static ExecutionOptions Execution(string profile, string adapter, string gateway)
    {
        var execution = new ExecutionOptions { DefaultProfile = profile };
        execution.Profiles[profile] = new ExecutionProfileOptions { Adapter = adapter, Gateway = gateway };
        execution.AgentTypeProfiles["Implementation"] = profile;
        execution.AgentTypeProfiles["Review"] = profile;
        execution.Validate();
        return execution;
    }

    private IAgentRuntime HermesRuntime(
        ScriptedHermesProcess process, string gateway, string hermesProvider, bool omniRouteEnabled = true)
    {
        var execution = Execution("Hermes", HermesExecutionAdapter.AdapterKey, gateway);
        var hermesOptions = new HermesOptions
        {
            DockerImage = "test/hermes:pinned",
            NetworkMode = "bridge",
            Provider = hermesProvider,
            TimeoutSeconds = 60,
            WorkspaceRoot = Path.Combine(_workspace, "scratch")
        };

        // Phase K — Hermes has no verified built-in endpoint for `openai`; a route to OmniRoute names
        // the gateway's endpoint explicitly, as a real deployment must.
        if (hermesProvider == "openai")
        {
            hermesOptions.AdditionalEnvironmentVariables["OPENAI_BASE_URL"] = OmniRouteGateway.DefaultBaseUrl;
        }

        var classifier = new FailureClassifier();
        var executor = new HermesAgentExecutor(
            hermesOptions, process, NullLogger<HermesAgentExecutor>.Instance,
            gatewayResolver: Resolver(omniRouteEnabled, execution));
        var adapter = new HermesExecutionAdapter(executor, classifier, NullLogger<HermesExecutionAdapter>.Instance);

        return new AdapterDispatchingAgentRuntime(
            new ExecutionProfileResolver(execution, new ExecutionAdapterRegistry(new IExecutionAdapter[] { adapter })),
            classifier,
            NullLogger<AdapterDispatchingAgentRuntime>.Instance);
    }

    private IAgentRuntime OpenHandsRuntime(ScriptedOpenHandsClient client)
    {
        var execution = Execution("OpenHands", OpenHandsExecutionAdapter.AdapterKey, OpenRouterGateway.Key);
        var classifier = new FailureClassifier();
        var adapter = new OpenHandsExecutionAdapter(
            new OpenHandsOptions { BaseUrl = "http://127.0.0.1:48080", PollIntervalSeconds = 0.01 },
            new NoProcessRunner(),
            new NoContainerLifecycle(),
            classifier,
            NullLogger<OpenHandsExecutionAdapter>.Instance,
            clientFactory: new ScriptedOpenHandsClientFactory(client),
            secretProvider: new FakeSecretProvider(new Dictionary<string, string?> { ["OPENROUTER_API_KEY"] = "test-openrouter-key" }),
            gatewayResolver: Resolver(omniRouteEnabled: false, execution));

        return new AdapterDispatchingAgentRuntime(
            new ExecutionProfileResolver(execution, new ExecutionAdapterRegistry(new IExecutionAdapter[] { adapter })),
            classifier,
            NullLogger<AdapterDispatchingAgentRuntime>.Instance);
    }

    private async Task<(Guid RunId, Guid WorkItemId)> SeedAsync()
    {
        await using var db = new FactoryDbContext(_options);

        var definition = await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = "Coder",
            Description = "Implements WorkItems",
            AgentType = "Implementation",
            SystemPrompt = "You are a coding agent."
        });

        var projects = new ProjectRepository(db);
        var project = new Project
        {
            Name = "Gateway E2E",
            Slug = "gateway-e2e-" + Guid.NewGuid().ToString("N"),
            RootPath = _workspace,
            Status = ProjectStatus.Analyzing
        };
        await projects.AddAsync(project);
        await projects.SaveChangesAsync();

        var workItems = new WorkItemService(new WorkItemRepository(db), projects);
        var item = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, "Implement", null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Running);

        var runs = new AgentRunRepository(db);
        var run = await runs.CreateAsync(new AgentRun
        {
            AgentDefinitionId = definition.Id,
            ProjectId = project.Id,
            WorkItemId = item.Id,
            Input = "Implement it.",
            Model = Model
        });
        run.TransitionTo(AgentRunStatus.Queued);
        await runs.UpdateAsync(run);

        return (run.Id, item.Id);
    }

    private async Task<AgentOrchestrationResult> OrchestrateAsync(Guid runId, IAgentRuntime runtime)
    {
        await using var db = new FactoryDbContext(_options);
        var orchestrator = new AgentOrchestrator(
            new AgentRunRepository(db),
            new ProjectRepository(db),
            new FakeAgentToolRegistry(),
            new FakeAgentMemory(),
            runtime,
            new ProjectWorkspaceResolver(new AgentWorkspaceOptions()),
            new AgentInstructionBuilder(),
            new StructuredAgentResultParser(),
            NullLogger<AgentOrchestrator>.Instance,
            null,
            null,
            new FileSystemArtifactRecorder(db),
            new ArtifactRepository(db));

        return await orchestrator.ExecuteAsync(runId);
    }

    private async Task<(AgentRun Run, List<Artifact> Artifacts, StructuredAgentResult Structured)> LoadAsync(Guid runId)
    {
        await using var db = new FactoryDbContext(_options);
        var run = (await new AgentRunRepository(db).GetByIdAsync(runId))!;
        var artifacts = await db.Artifacts.ToListAsync();
        var structured = JsonSerializer.Deserialize<StructuredAgentResult>(run.StructuredResult!, StructuredAgentResult.JsonOptions)!;
        return (run, artifacts, structured);
    }

    private async Task<(WorkItem WorkItem, RecordingVerificationGate Gate, RecordingCompletionProcessor Completion)> SyncAsync(Guid runId)
    {
        await using var db = new FactoryDbContext(_options);
        var run = (await new AgentRunRepository(db).GetByIdAsync(runId))!;
        var gate = new RecordingVerificationGate();
        var completion = new RecordingCompletionProcessor();

        await new AgentRunWorkItemSync(
            new WorkItemRepository(db), new ProjectRepository(db), new WorkItemExecutionResultMapper(),
            gate, completion, NullLogger<AgentRunWorkItemSync>.Instance).SyncAsync(run);

        var workItem = (await new WorkItemRepository(db).GetByIdAsync(run.WorkItemId!.Value))!;
        return (workItem, gate, completion);
    }

    // ---------------------------------------------------------------- fakes

    /// <summary>`docker run` of Hermes: writes the --usage-file report into the mounted workspace, as Hermes does.</summary>
    private sealed class ScriptedHermesProcess : IProcessRunner
    {
        private readonly int _exitCode;
        private readonly string _stdout;
        private readonly bool? _reportFailed;
        private readonly int? _inputTokens;
        private readonly int? _outputTokens;
        private readonly int? _totalTokens;
        private readonly bool? _reportCompleted;

        /// <param name="reportFailed">null: Hermes leaves no run report.</param>
        /// <param name="reportCompleted">Defaults to the opposite of <paramref name="reportFailed"/>.</param>
        public ScriptedHermesProcess(
            int exitCode, string stdout, bool? reportFailed,
            int? inputTokens = null, int? outputTokens = null, int? totalTokens = null, bool? reportCompleted = null)
        {
            _exitCode = exitCode;
            _stdout = stdout;
            _reportFailed = reportFailed;
            _reportCompleted = reportCompleted;
            _inputTokens = inputTokens;
            _outputTokens = outputTokens;
            _totalTokens = totalTokens;
        }

        public List<ProcessRunRequest> Runs { get; } = new();

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Arguments.Count == 0 || request.Arguments[0] != "run")
            {
                return Task.FromResult(new ProcessRunResult(0, "", "", TimedOut: false));
            }

            Runs.Add(request);

            if (_reportFailed is { } failed)
            {
                var args = request.Arguments.ToList();
                var containerPath = args[args.IndexOf("--usage-file") + 1];
                var hostPath = Path.Combine(request.WorkingDirectory!, containerPath["/workspace/".Length..]);
                File.WriteAllText(hostPath, JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["failed"] = failed,
                    ["completed"] = _reportCompleted ?? !failed,
                    ["input_tokens"] = _inputTokens,
                    ["output_tokens"] = _outputTokens,
                    ["total_tokens"] = _totalTokens,
                    ["provider"] = "scripted",
                    ["model"] = Model
                }));
            }

            return Task.FromResult(new ProcessRunResult(_exitCode, _stdout, "", TimedOut: false));
        }
    }

    private sealed class ScriptedOpenHandsClient : IOpenHandsClient
    {
        private readonly Queue<string> _statuses;

        public ScriptedOpenHandsClient(params string[] statuses) => _statuses = new Queue<string>(statuses);

        public OpenHandsCreateConversationRequest? CreateRequest { get; private set; }

        public OpenHandsTokenUsageDto? Usage { get; init; }

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<OpenHandsConversationInfo> CreateConversationAsync(
            OpenHandsCreateConversationRequest request, CancellationToken cancellationToken = default)
        {
            CreateRequest = request;
            return Task.FromResult(new OpenHandsConversationInfo { Id = "conversation-1", ExecutionStatus = "idle" });
        }

        public Task<bool> SendMessageAsync(
            string conversationId, string message, bool run = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<OpenHandsConversationInfo> GetConversationAsync(
            string conversationId, CancellationToken cancellationToken = default)
        {
            var info = new OpenHandsConversationInfo
            {
                Id = conversationId,
                ExecutionStatus = _statuses.Count > 1 ? _statuses.Dequeue() : _statuses.Peek()
            };

            if (Usage is not null)
            {
                info.Stats = new OpenHandsStatsDto
                {
                    UsageToMetrics = new Dictionary<string, OpenHandsUsageMetricDto>
                    {
                        ["agent"] = new() { ModelName = Model, AccumulatedTokenUsage = Usage }
                    }
                };
            }

            return Task.FromResult(info);
        }

        public Task<string?> GetFinalResponseAsync(string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("done");

        public Task<bool> InterruptAsync(string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class ScriptedOpenHandsClientFactory : IOpenHandsClientFactory
    {
        private readonly IOpenHandsClient _client;

        public ScriptedOpenHandsClientFactory(IOpenHandsClient client) => _client = client;

        public IOpenHandsClient CreateClient(string serverUrl, string? sessionApiKey) => _client;
    }

    private sealed class NoProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessRunResult(0, "", "", TimedOut: false));
    }

    private sealed class NoContainerLifecycle : IContainerLifecycleManager
    {
        public Task EnsureCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> SweepOrphanContainersAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    /// <summary>Would pass anything it is given — so any call proves a fabricated acceptance path.</summary>
    private sealed class RecordingVerificationGate : IVerificationGate
    {
        public int Calls { get; private set; }

        public Task<VerificationResult> VerifyAsync(Project project, WorkItem workItem, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new VerificationResult(VerificationStatus.Passed, "fake:Passed", 0, TimeSpan.Zero));
        }

        public Task<VerificationResult> VerifyProjectAsync(Project project, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new VerificationResult(VerificationStatus.Passed, "fake:Passed", 0, TimeSpan.Zero));
        }
    }

    private sealed class RecordingCompletionProcessor : IWorkItemCompletionProcessor
    {
        public int Calls { get; private set; }

        public Task<WorkItemCompletionDecision> ProcessAsync(
            Project project, WorkItem workItem, AgentRun agentRun, VerificationResult verificationResult,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new WorkItemCompletionDecision(WorkItemStatus.Completed, null));
        }
    }
}
