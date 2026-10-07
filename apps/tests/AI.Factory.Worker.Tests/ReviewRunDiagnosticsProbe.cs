using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Application.Orchestration;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Configuration;
using AI.Factory.Infrastructure.Gateways;
using AI.Factory.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase P.1 diagnostic probe (temporary, live opt-in): reproduces exactly one real Hermes run of
/// the review-executor path — fresh isolated scratch workspace, seeded REVIEW bundle files, the
/// review prompt — and prints the raw exit code, error and both streams. It exists to attribute the
/// reviewer failure observed in certification attempt H1 (exit non-zero, start-up log only) with
/// observed data instead of inference. Billed; opt-in with AI_FACTORY_LIVE_CERT_HERMES.
/// </summary>
public sealed class ReviewRunDiagnosticsProbe
{
    private readonly ITestOutputHelper _output;

    public ReviewRunDiagnosticsProbe(ITestOutputHelper output) => _output = output;

    [LiveFact("AI_FACTORY_LIVE_CERT_HERMES", RequiredEnvironment = new[] { "OPENROUTER_API_KEY" }, RequiresDocker = true)]
    public async Task Probe_HermesReviewExecutorRun_WithSeededBundle_ReportsRawStreams()
    {
        const string model = "deepseek/deepseek-v4-flash-0731";
        var scratch = Directory.CreateTempSubdirectory("aif-review-probe-");

        try
        {
            var options = new HermesOptions
            {
                DockerImage = "nousresearch/hermes-agent:0.20.6",
                NetworkMode = "bridge",
                Provider = "openrouter",
                ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
                ProviderApiKeySecretName = "OPENROUTER_API_KEY",
                TimeoutSeconds = 600,
                WorkspaceRoot = scratch.FullName,
                DefaultModel = model
            };
            options.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = "https://openrouter.ai/api/v1";
            options.AdditionalEnvironmentVariables["HERMES_WRITE_SAFE_ROOT"] = "/workspace";

            var registry = new ModelGatewayRegistry();
            registry.Register(new OpenRouterGateway());
            var gatewayOptions = new ModelGatewayOptions
            {
                DefaultGateway = OpenRouterGateway.Key,
                Gateways = { [OpenRouterGateway.Key] = new GatewayEntryOptions { BaseUrl = OpenRouterGateway.DefaultBaseUrl } }
            };

            var executor = new HermesAgentExecutor(
                options,
                new SystemProcessRunner(),
                NullLogger<HermesAgentExecutor>.Instance,
                secretProvider: new EnvironmentSecretProvider(NullLogger<EnvironmentSecretProvider>.Instance),
                gatewayResolver: new ModelGatewayResolver(registry, gatewayOptions));

            var seeds = new Dictionary<string, string>
            {
                ["REVIEW/context.md"] =
                    "# Review context\n\nRequirements: add Calculator.Multiply and a test.\n" +
                    "Acceptance criteria: Multiply present; Add unchanged; Multiply_ReturnsProduct present; tests pass.\n" +
                    "Verification: build succeeded, tests succeeded (Factory-run).",
                ["REVIEW/changes.md"] =
                    "## src/Calculator.cs [Modified]\n" +
                    "--- baseline\npublic static int Add(int a, int b) => a + b;\n" +
                    "+++ final\npublic static int Add(int a, int b) => a + b;\npublic static int Multiply(int a, int b) => a * b;\n\n" +
                    "## tests/CalculatorTests.cs [Modified]\n" +
                    "--- baseline\n[Fact]\npublic void Add_ReturnsSum() => Assert.Equal(5, Calculator.Add(2, 3));\n" +
                    "+++ final\n[Fact]\npublic void Add_ReturnsSum() => Assert.Equal(5, Calculator.Add(2, 3));\n" +
                    "[Fact]\npublic void Multiply_ReturnsProduct() => Assert.Equal(42, Calculator.Multiply(6, 7));"
            };

            const string prompt =
                """
                You are an independent code reviewer. You did not write this implementation.
                Evaluate correctness, requirements compliance, architecture, security, error
                handling, test coverage, maintainability, and unnecessary complexity.

                The exact source changes are in your workspace as review bundle files:
                - REVIEW/context.md — requirements, acceptance criteria, verification results and the changed-file list
                - REVIEW/changes.md — the exact source changes (unified diff and/or full file contents) for every changed file
                Read BOTH files first. They are the authoritative content; the file-name list below is only a summary.

                Changed file summary (2 change(s) from git-baseline):
                src/Calculator.cs [Modified]
                tests/CalculatorTests.cs [Modified]

                Build succeeded: True
                Tests succeeded: True

                Respond with EXACTLY the following machine-readable frame and nothing before or after it:

                <<<FACTORY_REVIEW_RESULT>>>
                [{"severity":"Critical|High|Medium|Low|Info","category":"Correctness|RequirementsCompliance|Architecture|Security|ErrorHandling|TestCoverage|Maintainability|UnnecessaryComplexity","file":"path/relative.cs","line":1,"description":"what and why","recommendation":"concrete fix"}]
                <<<END_FACTORY_REVIEW_RESULT>>>

                Rules for the payload between the markers:
                - It must be a single JSON array of finding objects (fields: severity, category, file, line,
                  description, recommendation; file/line may be null). Return [] when there are no findings.
                - Do not put anything else between the markers.
                - A missing, empty or malformed payload is recorded as a FAILED review, never as "no findings".
                """;

            var request = new AgentExecutionRequest(
                Agent: "reviewer",
                Model: model,
                Prompt: prompt,
                WorkingDirectory: string.Empty,
                AgentRunId: Guid.NewGuid(),
                Adapter: "Hermes",
                SeedFiles: seeds);

            var result = await executor.ExecuteAsync(request);

            _output.WriteLine($"PROBE success={result.Success} exitCode={result.ExitCode?.ToString() ?? "-"} durationMs={result.DurationMs}");
            _output.WriteLine("PROBE error=" + (result.Error ?? "-"));
            _output.WriteLine($"PROBE output.length={result.Output?.Length ?? 0}");
            _output.WriteLine("PROBE output.tail=" + Tail(result.Output, 3000));
            _output.WriteLine("PROBE report=" + (result.Report is null ? "-" : $"runtime={result.Report.Runtime} exit={result.Report.ExitCode} model={result.Report.ReportedModel}"));

            var seeded = Directory.EnumerateFiles(scratch.FullName, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(scratch.FullName, p).Replace('\\', '/'));
            _output.WriteLine("PROBE workspace.after=[" + string.Join(",", seeded) + "]");

            Assert.True(result.ExitCode is not null, "No exit code was observed — the run did not reach a process exit.");
        }
        finally
        {
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Tail(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "-" : (text.Length <= max ? text : "[…]" + text[^max..]).ReplaceLineEndings("\\n");

    /// <summary>
    /// The full production review chain (AgentCodeReviewer → AdapterDispatchingAgentRuntime →
    /// HermesExecutionAdapter → HermesAgentExecutor) against the real image, with a canary in the
    /// bundle: the review result and the scratch workspace tree after the run prove whether the
    /// bundle was seeded, whether the model could read it, and whether it reported that truthfully.
    /// </summary>
    [LiveFact("AI_FACTORY_LIVE_CERT_HERMES", RequiredEnvironment = new[] { "OPENROUTER_API_KEY" }, RequiresDocker = true)]
    public async Task Probe_FullReviewChain_WithCanaryBundle_ReportsBundleVisibility()
    {
        const string model = "deepseek/deepseek-v4-flash-0731";
        var scratch = Directory.CreateTempSubdirectory("aif-review-chain-");

        try
        {
            var options = new HermesOptions
            {
                DockerImage = "nousresearch/hermes-agent:0.20.6",
                NetworkMode = "bridge",
                Provider = "openrouter",
                ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
                ProviderApiKeySecretName = "OPENROUTER_API_KEY",
                TimeoutSeconds = 600,
                WorkspaceRoot = scratch.FullName,
                DefaultModel = model
            };
            options.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = "https://openrouter.ai/api/v1";
            options.AdditionalEnvironmentVariables["HERMES_WRITE_SAFE_ROOT"] = "/workspace";

            var registry = new ModelGatewayRegistry();
            registry.Register(new OpenRouterGateway());
            var gatewayOptions = new ModelGatewayOptions
            {
                DefaultGateway = OpenRouterGateway.Key,
                Gateways = { [OpenRouterGateway.Key] = new GatewayEntryOptions { BaseUrl = OpenRouterGateway.DefaultBaseUrl } }
            };

            var execution = new ExecutionOptions { DefaultProfile = "Hermes" };
            execution.Profiles["Hermes"] = new ExecutionProfileOptions { Adapter = "Hermes", Gateway = "OpenRouter" };
            execution.Validate();

            var classifier = new AI.Factory.Application.Abstractions.FailureClassifier();
            var executor = new HermesAgentExecutor(
                options,
                new SystemProcessRunner(),
                NullLogger<HermesAgentExecutor>.Instance,
                secretProvider: new EnvironmentSecretProvider(NullLogger<EnvironmentSecretProvider>.Instance),
                gatewayResolver: new ModelGatewayResolver(registry, gatewayOptions));
            var adapter = new HermesExecutionAdapter(executor, classifier, NullLogger<HermesExecutionAdapter>.Instance);
            var runtime = new AdapterDispatchingAgentRuntime(
                new ExecutionProfileResolver(execution, new ExecutionAdapterRegistry(new IExecutionAdapter[] { adapter })),
                classifier,
                NullLogger<AdapterDispatchingAgentRuntime>.Instance);

            var routing = new ModelRoutingOptions
            {
                Models =
                {
                    new ModelCapability
                    {
                        ModelId = model, Provider = "openrouter", ReasoningStrength = 4,
                        MaxContextTokens = 32_000, CostTier = 1, IsCodingOptimized = true, IsAvailable = true
                    }
                },
                CategoryProfiles = { ["code-review"] = new List<string> { model } }
            };

            var reviewer = new AgentCodeReviewer(runtime, new ModelRouter(routing), NullLogger<AgentCodeReviewer>.Instance);

            // The canary diff contains a DELIBERATE bug (Multiply adds instead of multiplying): a
            // reviewer that genuinely read the bundle must flag it — so the probe's verdict about
            // bundle visibility is unambiguous ([] from a non-reading model would not be).
            var bundle = new ReviewBundle(
                ReviewBundle.SourceGitBaseline,
                new[] { new ReviewFileChange("src/Calculator.cs", ReviewChangeKind.Modified, "public static int Add(int a, int b) => a + b;", "public static int Add(int a, int b) => a + b;\npublic static int Multiply(int a, int b) => a + b; // BUG CANARY-42: adds instead of multiplying") },
                "# Review context (canary: CANARY-42)\nRequirements: add Multiply (must return a*b) with a test; Factory verified build+tests pass.",
                "## src/Calculator.cs [Modified]\n--- baseline\npublic static int Add(int a, int b) => a + b;\n+++ final\npublic static int Add(int a, int b) => a + b;\npublic static int Multiply(int a, int b) => a + b; // BUG CANARY-42: adds instead of multiplying");

            var context = new ReviewContext(
                WorkItemId: Guid.NewGuid(), ProjectId: Guid.NewGuid(), RequirementsText: "Add Calculator.Multiply.",
                AcceptanceCriteria: "Multiply present; tests pass.", ArchitectureText: "Single static class.",
                ChangedFiles: new[] { "src/Calculator.cs" }, TestOutput: "Passed! - Failed: 0, Passed: 2",
                TestsSucceeded: true, BuildOutput: "Build succeeded.", BuildSucceeded: true, TaskHistoryText: "",
                Bundle: bundle);

            var review = await reviewer.ReviewAsync(context);

            _output.WriteLine($"PROBE2 review.status={review.Status} blocks={review.BlocksCompletion} findings={review.Findings.Count}");
            foreach (var finding in review.Findings)
            {
                _output.WriteLine("PROBE2 finding[" + finding.Severity + "]: " + TruncateLine(finding.Description, 1200));
            }

            var tree = Directory.EnumerateFileSystemEntries(scratch.FullName, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(scratch.FullName, p).Replace('\\', '/'));
            _output.WriteLine("PROBE2 workspace.tree=[" + string.Join(",", tree) + "]");

            foreach (var file in Directory.EnumerateFiles(scratch.FullName, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(scratch.FullName, file).Replace('\\', '/');
                _output.WriteLine($"PROBE2 file={relative} bytes={new FileInfo(file).Length}");
            }
        }
        finally
        {
            TryDelete(scratch.FullName);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string TruncateLine(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "-" : (text.Length <= max ? text : text[..max] + "[…]").ReplaceLineEndings("\\n");
}
