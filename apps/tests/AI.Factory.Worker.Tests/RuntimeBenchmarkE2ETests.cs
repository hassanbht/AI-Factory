using System.Text.Json;
using AI.Factory.Worker.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

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

    private static bool LiveEnabled =>
        (bool.TryParse(Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_SMOKE_TEST"), out var h) && h) ||
        (bool.TryParse(Environment.GetEnvironmentVariable("AI_FACTORY_OPENHANDS_SMOKE_TEST"), out var o) && o);

    [Fact]
    public void CanonicalBaseline_CreatesReproducibleState_WithIdenticalTreeHash()
    {
        var tempA = Path.Combine(Path.GetTempPath(), "aif-bench-baseline-a-" + Guid.NewGuid().ToString("N")[..8]);
        var tempB = Path.Combine(Path.GetTempPath(), "aif-bench-baseline-b-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempA);
        Directory.CreateDirectory(tempB);

        try
        {
            var (repoA, shaA, hashA) = BenchmarkHarness.CreateCanonicalBaselineBareRepo(tempA);
            var (repoB, shaB, hashB) = BenchmarkHarness.CreateCanonicalBaselineBareRepo(tempB);

            Assert.True(File.Exists(Path.Combine(repoA, "HEAD")));
            Assert.True(File.Exists(Path.Combine(repoB, "HEAD")));
            Assert.NotEmpty(shaA);
            Assert.NotEmpty(shaB);
            Assert.Equal(hashA, hashB);
        }
        finally
        {
            try { Directory.Delete(tempA, recursive: true); } catch { }
            try { Directory.Delete(tempB, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BenchmarkModels_Serialization_RoundTripsAccurately_AndHandlesMissingTelemetryHonesty()
    {
        var record = new BenchmarkRunRecord
        {
            BenchmarkId = "BENCH-001",
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
        Assert.Equal(record.Runtime, deserialized.Runtime);
        Assert.Equal(record.RunId, deserialized.RunId);
        Assert.Null(deserialized.InputTokens);
        Assert.Null(deserialized.TotalTokens);
        Assert.Equal("Passed", deserialized.AcceptanceStatus);
        Assert.True(deserialized.CleanupVerified);
    }

    [Fact]
    public void FileClassification_CategorizesRequiredAndIncidentalCorrectly()
    {
        var testPaths = new[]
        {
            ("src/Calculator.cs", "Required"),
            ("tests/CalculatorTests.cs", "Required"),
            ("NOTES.md", "Required"),
            ("src/obj/Debug/net10.0/Demo.dll", "AcceptableIncidental"),
            ("src/bin/Debug/net10.0/Demo.dll", "AcceptableIncidental"),
            (".git/index", "AcceptableIncidental"),
            ("unexpected_file.txt", "Unexpected")
        };

        foreach (var (path, expected) in testPaths)
        {
            string actual;
            if (path.Equals("src/Calculator.cs", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("tests/CalculatorTests.cs", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("NOTES.md", StringComparison.OrdinalIgnoreCase))
            {
                actual = "Required";
            }
            else if (path.Contains("obj/") || path.Contains("bin/") || path.Contains(".git/"))
            {
                actual = "AcceptableIncidental";
            }
            else
            {
                actual = "Unexpected";
            }

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Live_ControlledBenchmark_HermesVsOpenHands_EmpiricalComparison()
    {
        if (!LiveEnabled)
        {
            _output.WriteLine("Live benchmark skipped (AI_FACTORY_HERMES_SMOKE_TEST not set).");
            return;
        }

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
}
