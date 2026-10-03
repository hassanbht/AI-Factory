using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.OpenHands;
using AI.Factory.TestSupport;
using AI.Factory.Worker.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using AI.Factory.TestSupport;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase F — Controlled Hermes vs OpenHands Empirical Runtime Comparison Test Suite.
/// Tests both the deterministic baseline invariants and executes the live benchmark
/// runs under controlled conditions.
/// </summary>
public sealed class RuntimeBenchmarkE2ETests
{
    private readonly ITestOutputHelper _output;

    public RuntimeBenchmarkE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LiveFact("AI_FACTORY_LIVE_BENCHMARK", "AI_FACTORY_RUN_BENCHMARK", RequiredEnvironment = new[] { "OPENROUTER_API_KEY" })]
    public async Task CheckOpenRouterConnectivity()
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        var resp = await client.GetAsync("https://openrouter.ai/api/v1/auth/key");
        Assert.True(resp.IsSuccessStatusCode, $"OpenRouter auth failed: {resp.StatusCode}");
    }

    [Theory]
    [InlineData("TaskA")]
    [InlineData("TaskB")]
    [InlineData("TaskC")]
    [InlineData("TaskD")]
    public void CanonicalBaseline_CreatesReproducibleState_AcrossAllRepresentativeTaskCategories(string taskKey)
    {
        var task = taskKey switch
        {
            "TaskA" => BenchmarkTaskDefinition.TaskA_SingleFile(),
            "TaskB" => BenchmarkTaskDefinition.TaskB_MultiFile(),
            "TaskC" => BenchmarkTaskDefinition.TaskC_BugFix(),
            "TaskD" => BenchmarkTaskDefinition.TaskD_TestRepair(),
            _ => throw new ArgumentOutOfRangeException(nameof(taskKey))
        };

        var tempA = Path.Combine(Path.GetTempPath(), $"aif-bench-baseline-a-{task.TaskId}-" + Guid.NewGuid().ToString("N")[..8]);
        var tempB = Path.Combine(Path.GetTempPath(), $"aif-bench-baseline-b-{task.TaskId}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempA);
        Directory.CreateDirectory(tempB);

        try
        {
            var (repoA, shaA, hashA) = BenchmarkHarness.CreateCanonicalBaselineBareRepo(tempA, task);
            var (repoB, shaB, hashB) = BenchmarkHarness.CreateCanonicalBaselineBareRepo(tempB, task);

            Assert.True(File.Exists(Path.Combine(repoA, "HEAD")));
            Assert.True(File.Exists(Path.Combine(repoB, "HEAD")));
            Assert.NotEmpty(shaA);
            Assert.NotEmpty(shaB);
            Assert.Equal(shaA, shaB);
            Assert.Equal(hashA, hashB);
        }
        finally
        {
            TestTempDirectory.Delete(tempA, _output);
            TestTempDirectory.Delete(tempB, _output);
        }
    }

    [Fact]
    public void BenchmarkModels_Serialization_RoundTripsAccurately_AndHandlesMissingTelemetryHonesty()
    {
        var record = new BenchmarkRunRecord
        {
            BenchmarkId = "BENCH-001",
            TaskCategory = "MultiFileFeature",
            TaskTitle = "Implement Multiply in Calculator",
            Runtime = "Hermes",
            RunId = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            SourceCommit = "abc1234",
            BaselineHash = "hash999",
            Model = "deepseek/deepseek-v4-flash-0731",
            Gateway = "https://openrouter.ai/api/v1",
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            CompletedAtUtc = DateTime.UtcNow,
            ExecutionDurationSeconds = 45.2,
            TerminalStatus = "Succeeded",
            ExitReason = "exit:0",
            InputTokens = null,  // Honest null telemetry
            OutputTokens = null,
            TotalTokens = null,
            AcceptanceStatus = "Passed",
            VerificationDurationSeconds = 3.5,
            ChangedFiles = new List<string> { "src/Calculator.cs", "tests/CalculatorTests.cs", "NOTES.md" },
            FileClassifications = new Dictionary<string, string>
            {
                ["src/Calculator.cs"] = "Required",
                ["tests/CalculatorTests.cs"] = "Required",
                ["NOTES.md"] = "Required"
            },
            CleanupVerified = true
        };

        var json = record.ToJson();
        var deserialized = BenchmarkRunRecord.FromJson(json);

        Assert.NotNull(deserialized);
        Assert.Equal(record.BenchmarkId, deserialized!.BenchmarkId);
        Assert.Equal(record.TaskCategory, deserialized.TaskCategory);
        Assert.Equal(record.Runtime, deserialized.Runtime);
        Assert.Equal(record.RunId, deserialized.RunId);
        Assert.Null(deserialized.InputTokens);
        Assert.Null(deserialized.TotalTokens);
        Assert.Equal("Passed", deserialized.AcceptanceStatus);
        Assert.True(deserialized.CleanupVerified);
    }

    [Fact]
    public void FileClassification_CategorizesRequiredAndIncidentalCorrectly_ForEveryTask()
    {
        var tasks = new[]
        {
            BenchmarkTaskDefinition.TaskA_SingleFile(),
            BenchmarkTaskDefinition.TaskB_MultiFile(),
            BenchmarkTaskDefinition.TaskC_BugFix(),
            BenchmarkTaskDefinition.TaskD_TestRepair()
        };

        foreach (var task in tasks)
        {
            Assert.NotNull(task.FileClassifier);
            Assert.Equal("AcceptableIncidental", task.FileClassifier("src/obj/Debug/net10.0/Demo.dll"));
            Assert.Equal("AcceptableIncidental", task.FileClassifier("src/bin/Debug/net10.0/Demo.dll"));
            Assert.Equal("AcceptableIncidental", task.FileClassifier(".git/index"));
            Assert.Equal("Unexpected", task.FileClassifier("arbitrary_foreign_file.exe"));
        }
    }

    [Fact]
    public async Task CancellationMatrix_CancelBeforeLaunch_BothAdapters_TerminateCancelledWithoutContainers()
    {
        var tempWs = Path.Combine(Path.GetTempPath(), "aif-cancel-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempWs);

        try
        {
            var spec = new AgentExecutionSpec(
                AgentRunId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid(),
                ProjectId: Guid.NewGuid(),
                WorkItemId: Guid.NewGuid(),
                Attempt: 1,
                Iteration: 0,
                Agent: new ExecutionAgent(Guid.NewGuid(), "Coder", "Implementation", "sys"),
                Instructions: "inst",
                Workspace: new ExecutionWorkspace(tempWs),
                Model: new ModelRoute("deepseek/deepseek-v4-flash-0731", "openrouter"),
                Deadline: DateTimeOffset.UtcNow.AddMinutes(5));

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel before launch

            var classifier = new FailureClassifier();

            // 1. Hermes
            var hermesExecutor = new StubExecutor();
            var hermesAdapter = new HermesExecutionAdapter(
                hermesExecutor, classifier, NullLogger<HermesExecutionAdapter>.Instance);

            var hermesOutcome = await hermesAdapter.ExecuteAsync(spec, cts.Token);

            Assert.Equal(ExecutionOutcomeStatus.Cancelled, hermesOutcome.Status);
            Assert.Equal(HermesExecutionAdapter.ExitReasons.Cancelled, hermesOutcome.ExitReason);
            Assert.Equal(1, hermesExecutor.CallCount); // Delegated with token to executor

            // 2. OpenHands
            var ohRunner = new StubProcessRunner();
            var ohLifecycle = new StubLifecycleManager();
            var ohClientFactory = new StubClientFactory();
            var ohAdapter = new OpenHandsExecutionAdapter(
                new OpenHandsOptions(),
                ohRunner,
                ohLifecycle,
                classifier,
                NullLogger<OpenHandsExecutionAdapter>.Instance,
                ohClientFactory);

            var ohOutcome = await ohAdapter.ExecuteAsync(spec, cts.Token);

            Assert.Equal(ExecutionOutcomeStatus.Cancelled, ohOutcome.Status);
            Assert.Equal(OpenHandsExecutionAdapter.ExitReasons.Cancelled, ohOutcome.ExitReason);
            Assert.Contains(spec.AgentRunId, ohLifecycle.CleanedRuns); // Guaranteed cleanup verified
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TimeoutMatrix_ExpiredDeadlineBeforeLaunch_BothAdapters_TerminateTimedOutWithoutContainers()
    {
        var tempWs = Path.Combine(Path.GetTempPath(), "aif-timeout-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempWs);

        try
        {
            var expiredSpec = new AgentExecutionSpec(
                AgentRunId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid(),
                ProjectId: Guid.NewGuid(),
                WorkItemId: Guid.NewGuid(),
                Attempt: 1,
                Iteration: 0,
                Agent: new ExecutionAgent(Guid.NewGuid(), "Coder", "Implementation", "sys"),
                Instructions: "inst",
                Workspace: new ExecutionWorkspace(tempWs),
                Model: new ModelRoute("deepseek/deepseek-v4-flash-0731", "openrouter"),
                Deadline: DateTimeOffset.UtcNow.AddSeconds(-10)); // Expired deadline

            var classifier = new FailureClassifier();

            // 1. Hermes
            var hermesExecutor = new StubExecutor();
            var hermesAdapter = new HermesExecutionAdapter(
                hermesExecutor, classifier, NullLogger<HermesExecutionAdapter>.Instance);

            var hermesOutcome = await hermesAdapter.ExecuteAsync(expiredSpec, CancellationToken.None);

            Assert.Equal(ExecutionOutcomeStatus.TimedOut, hermesOutcome.Status);
            Assert.Equal(HermesExecutionAdapter.ExitReasons.Timeout, hermesOutcome.ExitReason);
            Assert.Equal(0, hermesExecutor.CallCount);

            // 2. OpenHands
            var ohRunner = new StubProcessRunner();
            var ohLifecycle = new StubLifecycleManager();
            var ohClientFactory = new StubClientFactory();
            var ohAdapter = new OpenHandsExecutionAdapter(
                new OpenHandsOptions(),
                ohRunner,
                ohLifecycle,
                classifier,
                NullLogger<OpenHandsExecutionAdapter>.Instance,
                ohClientFactory);

            var ohOutcome = await ohAdapter.ExecuteAsync(expiredSpec, CancellationToken.None);

            Assert.Equal(ExecutionOutcomeStatus.TimedOut, ohOutcome.Status);
            Assert.Equal(OpenHandsExecutionAdapter.ExitReasons.Timeout, ohOutcome.ExitReason);
            Assert.Equal(0, ohRunner.CallCount);
        }
        finally
        {
            try { Directory.Delete(tempWs, recursive: true); } catch { }
        }
    }

    private sealed class StubExecutor : IAgentExecutor
    {
        public int CallCount { get; private set; }
        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentExecutionResult(true, "Stub output", null, 100, null, 0));
        }
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        public int CallCount { get; private set; }
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty, false));
        }
    }

    private sealed class StubLifecycleManager : IContainerLifecycleManager
    {
        public List<Guid> CleanedRuns { get; } = new();
        public Task EnsureCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default)
        {
            CleanedRuns.Add(agentRunId);
            return Task.CompletedTask;
        }
        public Task<int> SweepOrphanContainersAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class StubClientFactory : IOpenHandsClientFactory
    {
        public IOpenHandsClient CreateClient(string serverUrl, string? sessionApiKey) => new StubOpenHandsClient();
    }

    private sealed class StubOpenHandsClient : IOpenHandsClient
    {
        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<OpenHandsConversationInfo> CreateConversationAsync(OpenHandsCreateConversationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenHandsConversationInfo { Id = "stub-conv", ExecutionStatus = "idle" });
        public Task<bool> SendMessageAsync(string conversationId, string message, bool run = true, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<OpenHandsConversationInfo> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenHandsConversationInfo { Id = conversationId, ExecutionStatus = "finished" });
        public Task<string?> GetFinalResponseAsync(string conversationId, CancellationToken cancellationToken = default) => Task.FromResult<string?>("Done");
        public Task<bool> InterruptAsync(string conversationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    [LiveFact("AI_FACTORY_LIVE_BENCHMARK", "AI_FACTORY_RUN_BENCHMARK", RequiresDocker = true, RequiredEnvironment = new[] { "OPENROUTER_API_KEY" })]
    public async Task Live_ControlledBenchmark_HermesVsOpenHands_EmpiricalComparison()
    {
        var baselineRoot = Path.Combine(Path.GetTempPath(), "aif-bench-master-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(baselineRoot);

        try
        {
            var (bareRepoPath, commitSha, baselineHash) = BenchmarkHarness.CreateCanonicalBaselineBareRepo(baselineRoot);
            _output.WriteLine($"[BENCHMARK BASELINE] Commit: {commitSha}, BaselineHash: {baselineHash}");

            var hermesRuns = new List<BenchmarkRunRecord>();
            var openHandsRuns = new List<BenchmarkRunRecord>();

            const int runsPerRuntime = 3;
            var model = BenchmarkHarness.ControlledModel;

            // 1. Execute Hermes Runs
            for (int i = 1; i <= runsPerRuntime; i++)
            {
                _output.WriteLine($"--- Starting Hermes Run {i}/{runsPerRuntime} ---");
                var record = await BenchmarkHarness.ExecuteIterationAsync(
                    "Hermes", i, bareRepoPath, commitSha, baselineHash, model, output: _output);
                hermesRuns.Add(record);
                _output.WriteLine($"Hermes Run {i} completed: Status={record.TerminalStatus}, Acceptance={record.AcceptanceStatus}, Duration={record.ExecutionDurationSeconds:F1}s, Cleanup={record.CleanupVerified}");
            }

            // 2. Execute OpenHands Runs
            for (int i = 1; i <= runsPerRuntime; i++)
            {
                _output.WriteLine($"--- Starting OpenHands Run {i}/{runsPerRuntime} ---");
                var record = await BenchmarkHarness.ExecuteIterationAsync(
                    "OpenHands", i, bareRepoPath, commitSha, baselineHash, model, output: _output);
                openHandsRuns.Add(record);
                _output.WriteLine($"OpenHands Run {i} completed: Status={record.TerminalStatus}, Acceptance={record.AcceptanceStatus}, Duration={record.ExecutionDurationSeconds:F1}s, Cleanup={record.CleanupVerified}");
            }

            // 3. Summarize and assert benchmark invariants
            var summary = new BenchmarkComparisonSummary
            {
                BenchmarkId = BenchmarkHarness.BenchmarkId,
                Model = model,
                Gateway = BenchmarkHarness.ControlledGateway,
                CanonicalSourceCommit = commitSha,
                Validity = "CONTROLLED",
                ValidityReason = "Both runtimes executed the same deterministic WorkItem against identical baseline repository copies, using the exact same OpenRouter model (deepseek/deepseek-v4-flash-0731) and identical Factory verification rules.",
                HermesRuns = hermesRuns,
                OpenHandsRuns = openHandsRuns
            };

            // Write report to output
            _output.WriteLine("\n================ PHASE F BENCHMARK RESULTS ================");
            _output.WriteLine($"BenchmarkId: {summary.BenchmarkId}");
            _output.WriteLine($"Validity: {summary.Validity} ({summary.ValidityReason})");
            _output.WriteLine($"Model: {summary.Model} | Gateway: {summary.Gateway}");
            _output.WriteLine("");

            var hermesSuccess = hermesRuns.Count(r => r.TerminalStatus == "Succeeded");
            var openHandsSuccess = openHandsRuns.Count(r => r.TerminalStatus == "Succeeded");
            var hermesPassed = hermesRuns.Count(r => r.AcceptanceStatus == "Passed");
            var openHandsPassed = openHandsRuns.Count(r => r.AcceptanceStatus == "Passed");
            var hermesFailed = hermesRuns.Count(r => r.AcceptanceStatus == "Failed");
            var openHandsFailed = openHandsRuns.Count(r => r.AcceptanceStatus == "Failed");
            var hermesRepairs = hermesRuns.Sum(r => r.RepairCount);
            var openHandsRepairs = openHandsRuns.Sum(r => r.RepairCount);
            var hermesMedian = BenchmarkComparisonSummary.CalculateMedian(hermesRuns.Select(r => r.ExecutionDurationSeconds));
            var openHandsMedian = BenchmarkComparisonSummary.CalculateMedian(openHandsRuns.Select(r => r.ExecutionDurationSeconds));
            var hermesMin = hermesRuns.Any() ? hermesRuns.Min(r => r.ExecutionDurationSeconds) : 0;
            var openHandsMin = openHandsRuns.Any() ? openHandsRuns.Min(r => r.ExecutionDurationSeconds) : 0;
            var hermesMax = hermesRuns.Any() ? hermesRuns.Max(r => r.ExecutionDurationSeconds) : 0;
            var openHandsMax = openHandsRuns.Any() ? openHandsRuns.Max(r => r.ExecutionDurationSeconds) : 0;
            var hermesRetries = hermesRuns.Sum(r => r.RetryCount);
            var openHandsRetries = openHandsRuns.Sum(r => r.RetryCount);
            var hermesTokens = hermesRuns.All(r => r.TotalTokens == null) ? "NOT AVAILABLE" : hermesRuns.Sum(r => r.TotalTokens ?? 0).ToString();
            var openHandsTokens = openHandsRuns.All(r => r.TotalTokens == null) ? "NOT AVAILABLE" : openHandsRuns.Sum(r => r.TotalTokens ?? 0).ToString();
            var hermesCleanup = hermesRuns.All(r => r.CleanupVerified);
            var openHandsCleanup = openHandsRuns.All(r => r.CleanupVerified);
            var hermesUnexpected = hermesRuns.Sum(r => r.FileClassifications.Count(kv => kv.Value == "Unexpected"));
            var openHandsUnexpected = openHandsRuns.Sum(r => r.FileClassifications.Count(kv => kv.Value == "Unexpected"));

            _output.WriteLine("| Metric | Hermes | OpenHands |");
            _output.WriteLine("|---|---:|---:|");
            _output.WriteLine($"| Runs executed | {hermesRuns.Count} | {openHandsRuns.Count} |");
            _output.WriteLine($"| Successful executions | {hermesSuccess} | {openHandsSuccess} |");
            _output.WriteLine($"| Acceptance passed | {hermesPassed} | {openHandsPassed} |");
            _output.WriteLine($"| Acceptance failed | {hermesFailed} | {openHandsFailed} |");
            _output.WriteLine($"| Repair runs | {hermesRepairs} | {openHandsRepairs} |");
            _output.WriteLine($"| Median execution duration | {hermesMedian:F1}s | {openHandsMedian:F1}s |");
            _output.WriteLine($"| Min execution duration | {hermesMin:F1}s | {openHandsMin:F1}s |");
            _output.WriteLine($"| Max execution duration | {hermesMax:F1}s | {openHandsMax:F1}s |");
            _output.WriteLine($"| Retry count | {hermesRetries} | {openHandsRetries} |");
            _output.WriteLine($"| Total tokens | {hermesTokens} | {openHandsTokens} |");
            _output.WriteLine($"| Cleanup verified | {hermesCleanup} | {openHandsCleanup} |");
            _output.WriteLine($"| Unexpected files | {hermesUnexpected} | {openHandsUnexpected} |");

            // Assertions on invariants
            Assert.NotEmpty(hermesRuns);
            Assert.NotEmpty(openHandsRuns);
            Assert.All(hermesRuns, r => Assert.True(r.CleanupVerified, "Hermes cleanup failed"));
            Assert.All(openHandsRuns, r => Assert.True(r.CleanupVerified, "OpenHands cleanup failed"));
        }
        finally
        {
            try { Directory.Delete(baselineRoot, recursive: true); } catch { }
        }
    }

    private async Task Live_ControlledBenchmark_RepresentativeWorkloadCategories(string taskKey)
    {
        var task = taskKey switch
        {
            "TaskA" => BenchmarkTaskDefinition.TaskA_SingleFile(),
            "TaskB" => BenchmarkTaskDefinition.TaskB_MultiFile(),
            "TaskC" => BenchmarkTaskDefinition.TaskC_BugFix(),
            "TaskD" => BenchmarkTaskDefinition.TaskD_TestRepair(),
            _ => throw new ArgumentOutOfRangeException(nameof(taskKey))
        };

        var baselineRoot = Path.Combine(Path.GetTempPath(), $"aif-bench-cat-{taskKey}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(baselineRoot);

        try
        {
            var (bareRepoPath, commitSha, baselineHash) = BenchmarkHarness.CreateCanonicalBaselineBareRepo(baselineRoot, task);
            _output.WriteLine($"[BENCHMARK BASELINE {task.TaskId}] Commit: {commitSha}, BaselineHash: {baselineHash}");

            var hermesRuns = new List<BenchmarkRunRecord>();
            var openHandsRuns = new List<BenchmarkRunRecord>();

            const int runsPerRuntime = 1;
            var model = BenchmarkHarness.ControlledModel;

            // 1. Execute Hermes Runs
            for (int i = 1; i <= runsPerRuntime; i++)
            {
                _output.WriteLine($"--- Starting Hermes Run {i}/{runsPerRuntime} for {task.TaskId} ---");
                var record = await BenchmarkHarness.ExecuteIterationAsync(
                    "Hermes", i, bareRepoPath, commitSha, baselineHash, model, output: _output, task: task);
                hermesRuns.Add(record);
                _output.WriteLine($"Hermes Run {i} ({task.TaskId}): Status={record.TerminalStatus}, Acceptance={record.AcceptanceStatus}, Duration={record.ExecutionDurationSeconds:F1}s, Cleanup={record.CleanupVerified}");
            }

            // 2. Execute OpenHands Runs
            for (int i = 1; i <= runsPerRuntime; i++)
            {
                _output.WriteLine($"--- Starting OpenHands Run {i}/{runsPerRuntime} for {task.TaskId} ---");
                var record = await BenchmarkHarness.ExecuteIterationAsync(
                    "OpenHands", i, bareRepoPath, commitSha, baselineHash, model, output: _output, task: task);
                openHandsRuns.Add(record);
                _output.WriteLine($"OpenHands Run {i} ({task.TaskId}): Status={record.TerminalStatus}, Acceptance={record.AcceptanceStatus}, Duration={record.ExecutionDurationSeconds:F1}s, Cleanup={record.CleanupVerified}");
            }

            Assert.NotEmpty(hermesRuns);
            Assert.NotEmpty(openHandsRuns);
            Assert.All(hermesRuns, r => Assert.True(r.CleanupVerified, "Hermes cleanup failed"));
            Assert.All(openHandsRuns, r => Assert.True(r.CleanupVerified, "OpenHands cleanup failed"));
        }
        finally
        {
            try { Directory.Delete(baselineRoot, recursive: true); } catch { }
        }
    }

    [LiveFact("AI_FACTORY_LIVE_BENCHMARK", "AI_FACTORY_RUN_BENCHMARK", RequiresDocker = true, RequiredEnvironment = new[] { "OPENROUTER_API_KEY" })]
    public Task Live_Benchmark_TaskB_MultiFile() =>
        Live_ControlledBenchmark_RepresentativeWorkloadCategories("TaskB");

    [LiveFact("AI_FACTORY_LIVE_BENCHMARK", "AI_FACTORY_RUN_BENCHMARK", RequiresDocker = true, RequiredEnvironment = new[] { "OPENROUTER_API_KEY" })]
    public Task Live_Benchmark_TaskC_BugFix() =>
        Live_ControlledBenchmark_RepresentativeWorkloadCategories("TaskC");

    [LiveFact("AI_FACTORY_LIVE_BENCHMARK", "AI_FACTORY_RUN_BENCHMARK", RequiresDocker = true, RequiredEnvironment = new[] { "OPENROUTER_API_KEY" })]
    public Task Live_Benchmark_TaskD_TestRepair() =>
        Live_ControlledBenchmark_RepresentativeWorkloadCategories("TaskD");
}
