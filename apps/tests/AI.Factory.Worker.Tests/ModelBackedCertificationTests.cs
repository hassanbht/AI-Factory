using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.OpenHands;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.TestSupport;
using AI.Factory.Worker.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase P — model-backed runtime certification of the Docker runtimes through the real Factory path:
/// Project → WorkItem → TaskDispatchScheduler → AgentRunWorker → execution profile → adapter → container →
/// shipped OpenRouter gateway → one explicit model → verification gate → acceptance criteria → independent review → Completed.
///
/// Opt-in per runtime, one at a time (billed): <c>AI_FACTORY_LIVE_CERT_HERMES</c> or <c>AI_FACTORY_LIVE_CERT_OPENHANDS</c>, plus
/// <c>AI_FACTORY_CERT_MODEL</c> (an explicit model id; any <c>auto</c> id is rejected) and <c>OPENROUTER_API_KEY</c>.
///
/// Routing is explicit and has no fallback: every agent type (implementation, repair, review) uses the runtime under test, and
/// every routing category holds exactly the one certification model. The shipped gateway configuration is asserted, never
/// overridden. Nothing here retries or substitutes: a provider/model failure is reported as the failure it is.
///
/// Evidence is written to the test output (and to <c>AI_FACTORY_CERT_EVIDENCE_FILE</c> when set). The credential's value is
/// only ever compared, never written.
/// </summary>
[Collection(TestCollections.DockerAndBuilds)]
public sealed class ModelBackedCertificationTests
{
    private const string GatewayKey = "OpenRouter";
    private const string GatewayBaseUrl = "https://openrouter.ai/api/v1";
    private const string CredentialName = "OPENROUTER_API_KEY";

    private static readonly string[] RoutingCategories =
    {
        "requirements-analysis", "architecture", "planning", "coding", "debugging", "testing", "code-review", "documentation"
    };

    private static readonly string[] ExpectedChangedFiles = { "src/Calculator.cs", "tests/CalculatorTests.cs" };

    private static readonly string[] ForbiddenRuntimeState = { "conversations", "bash_events", "base_state.json", ".hermes", ".openhands" };

    private const string TaskTitle = "Add Calculator.Multiply with a unit test";

    private const string TaskDescription =
        "In this C# repository, make exactly these two changes and nothing else:\n" +
        "1. In src/Calculator.cs, add a public static method `Multiply(int a, int b)` to the existing `Calculator` class that returns a * b.\n" +
        "2. In tests/CalculatorTests.cs, add an xunit [Fact] test method named `Multiply_ReturnsProduct` to the existing " +
        "`CalculatorTests` class that asserts `Calculator.Multiply(6, 7)` equals 42.\n" +
        "Keep the existing Add method and its test unchanged. Do not create, delete or modify any other file. " +
        "Do not install software and do not change any global configuration.";

    private const string AcceptanceCriteria = """
        {"version":1,"criteria":[
          {"type":"ExpectedTextPresent","path":"src/Calculator.cs","value":"Multiply"},
          {"type":"ExpectedTextPresent","path":"src/Calculator.cs","value":"Add"},
          {"type":"ExpectedTextPresent","path":"tests/CalculatorTests.cs","value":"Multiply_ReturnsProduct"},
          {"type":"TestsPass"}
        ]}
        """;

    private readonly ITestOutputHelper _output;

    public ModelBackedCertificationTests(ITestOutputHelper output) => _output = output;

    [LiveFact("AI_FACTORY_LIVE_CERT_HERMES", RequiredEnvironment = new[] { "AI_FACTORY_CERT_MODEL", CredentialName }, RequiresDocker = true)]
    public Task Hermes_RealModel_CompletesTheCertificationTask_ThroughTheFactoryPath() => CertifyAsync("Hermes");

    [LiveFact("AI_FACTORY_LIVE_CERT_OPENHANDS", RequiredEnvironment = new[] { "AI_FACTORY_CERT_MODEL", CredentialName }, RequiresDocker = true)]
    public Task OpenHands_RealModel_CompletesTheCertificationTask_ThroughTheFactoryPath() => CertifyAsync("OpenHands");

    private async Task CertifyAsync(string runtime)
    {
        var model = Environment.GetEnvironmentVariable("AI_FACTORY_CERT_MODEL")!.Trim();
        var credential = Environment.GetEnvironmentVariable(CredentialName)!;

        // P2 — an automatic model id is never a certification route.
        Assert.False(
            model.StartsWith("auto", StringComparison.OrdinalIgnoreCase)
                || model.Contains("/auto", StringComparison.OrdinalIgnoreCase),
            $"'{model}' is an automatic model id; Phase P certifies explicit models only.");

        var started = DateTime.UtcNow;
        Evidence($"runtime={runtime} model={model} gateway={GatewayKey} baseUrl={GatewayBaseUrl} credential={CredentialName} (value never printed) startUtc={started:O}");

        // P9 — Factory-owned containers before; foreign containers recorded so they can be proven untouched.
        var before = DockerContainers();
        var factoryBefore = before.Where(c => c.Name.StartsWith(DockerContainerNaming.ContainerNamePrefix, StringComparison.Ordinal)).ToList();
        var foreignBefore = before.Where(c => !c.Name.StartsWith(DockerContainerNaming.ContainerNamePrefix, StringComparison.Ordinal)).ToList();
        Evidence($"containers.before factoryOwned={factoryBefore.Count} foreign={foreignBefore.Count}");
        Assert.Empty(factoryBefore);

        var tempDir = Path.Combine(Path.GetTempPath(), $"aif-cert-{runtime.ToLowerInvariant()}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(tempDir);
        var logs = new List<string>();

        try
        {
            var (bareRepo, seedCommit, seedHashes) = CreateCertificationRepository(tempDir);
            Evidence($"task.seedCommit={seedCommit} " + string.Join(" ", seedHashes.Select(h => $"{h.Key}#sha256={h.Value[..12]}")));

            var projectsBase = Path.Combine(tempDir, "workspaces");
            Directory.CreateDirectory(projectsBase);
            var dbPath = Path.Combine(tempDir, "factory.db");

            using var host = BuildHost(runtime, model, tempDir, projectsBase, dbPath, logs);

            var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite($"Data Source={dbPath}").Options;
            await using (var db = new FactoryDbContext(schemaOptions))
            {
                await db.Database.EnsureCreatedAsync();
            }

            var (projectId, workItemId, projectRoot) = await CreateProjectAndWorkItemAsync(host);
            Evidence($"projectId={projectId} workItemId={workItemId} workspace={projectRoot}");

            var worker = host.Services.GetServices<IHostedService>().OfType<AgentRunWorker>().Single();
            await worker.StartAsync(CancellationToken.None);

            WorkItem finalItem;
            try
            {
                finalItem = await DriveUntilSettledAsync(host, projectId, workItemId, TimeSpan.FromMinutes(30));
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }

            var ended = DateTime.UtcNow;
            Evidence($"workItem.final={finalItem.Status} repairAttempts={finalItem.RepairAttemptCount} error={Truncate(finalItem.CurrentError)} endUtc={ended:O} wallS={(ended - started).TotalSeconds:F0}");

            var (runs, reviewRuns) = await ReportPersistedEvidenceAsync(host, workItemId);

            // ── Cleanup (P9): immediately after the worker stopped, and again after the 10 s reconciliation window ──
            var factoryAfter = FactoryContainers();
            Evidence($"containers.afterImmediate factoryOwned={factoryAfter.Count}");
            await Task.Delay(TimeSpan.FromSeconds(15));
            var after = DockerContainers();
            var factoryLate = after.Where(c => c.Name.StartsWith(DockerContainerNaming.ContainerNamePrefix, StringComparison.Ordinal)).ToList();
            var foreignAfter = after.Where(c => !c.Name.StartsWith(DockerContainerNaming.ContainerNamePrefix, StringComparison.Ordinal)).ToList();
            var foreignChanged = foreignBefore
                .Where(b => !foreignAfter.Any(a => a.Id == b.Id && a.State == b.State))
                .Select(b => b.Name)
                .ToList();
            Evidence($"containers.after15s factoryOwned={factoryLate.Count} foreign={foreignAfter.Count} foreignChangedOrRemoved=[{string.Join(",", foreignChanged)}]");

            // ── Workspace (P10): changed files, runtime state, credential ──
            var changed = ChangedFiles(projectRoot, seedCommit);
            Evidence($"workspace.changedFiles=[{string.Join(",", changed.Select(DescribePath))}]");
            foreach (var unexpected in changed.Except(ExpectedChangedFiles))
            {
                Evidence("workspace.unexpected " + DescribeFile(projectRoot, unexpected));
            }
            var forbidden = FindForbiddenState(projectRoot);
            Evidence($"workspace.forbiddenRuntimeState=[{string.Join(",", forbidden)}]");
            var credentialFiles = FilesContaining(projectRoot, credential);
            Evidence($"workspace.filesContainingCredential={credentialFiles.Count}");
            var credentialLogLines = logs.Count(l => l.Contains(credential, StringComparison.Ordinal));
            Evidence($"logs.lines={logs.Count} linesContainingCredential={credentialLogLines}");
            var fallbackLines = logs.Where(l => l.Contains("fallback", StringComparison.OrdinalIgnoreCase)).ToList();
            Evidence($"logs.fallbackMentions={fallbackLines.Count}");
            foreach (var line in fallbackLines.Take(5))
            {
                Evidence("  " + Truncate(line));
            }
            var reviewLines = logs.Where(l => l.Contains("Independent review", StringComparison.OrdinalIgnoreCase)).ToList();
            Evidence($"logs.reviewMentions={reviewLines.Count}");
            foreach (var line in reviewLines)
            {
                Evidence("  " + Truncate(line, 500));
            }

            // ── Independent check of the artifact (not the agent's word, not the Factory's) ──
            var calculator = File.ReadAllText(Path.Combine(projectRoot, "src", "Calculator.cs"));
            var tests = File.ReadAllText(Path.Combine(projectRoot, "tests", "CalculatorTests.cs"));
            var calculatorChanged = Sha256(calculator) != seedHashes["src/Calculator.cs"];
            var testsChanged = Sha256(tests) != seedHashes["tests/CalculatorTests.cs"];
            Evidence($"artifact.calculatorChanged={calculatorChanged} containsMultiply={calculator.Contains("Multiply", StringComparison.Ordinal)} testsChanged={testsChanged} containsTest={tests.Contains("Multiply_ReturnsProduct", StringComparison.Ordinal)}");

            var focused = Run(projectRoot, "dotnet", "test", "--filter", "FullyQualifiedName~Multiply_ReturnsProduct");
            var focusedPassedOne = Regex.IsMatch(focused.Output, @"Passed:\s+1\b") && Regex.IsMatch(focused.Output, @"Failed:\s+0\b");
            var full = Run(projectRoot, "dotnet", "test");
            Evidence($"independent.focusedTest exit={focused.ExitCode} passedExactlyOne={focusedPassedOne}; fullSuite exit={full.ExitCode} {Regex.Match(full.Output, @"(Passed|Failed)!.*").Value.Trim()}");

            // ── Certification verdict: every condition observed, nothing inferred ──
            Assert.True(finalItem.Status == WorkItemStatus.Completed.ToString(),
                $"WorkItem ended {finalItem.Status}, not Completed. Error: {finalItem.CurrentError}");
            Assert.NotEmpty(runs);
            Assert.All(runs, r => Assert.Equal(model, r.Model));
            Assert.Contains(runs, r => r.Status == AgentRunStatus.Succeeded && ReadMetadata(r.StructuredResult).Adapter == runtime);
            Assert.True(calculatorChanged && testsChanged, "The model did not change both requested files.");
            Assert.Equal(ExpectedChangedFiles, changed);
            Assert.Empty(forbidden);
            Assert.Empty(credentialFiles);
            Assert.Equal(0, credentialLogLines);
            Assert.True(focused.ExitCode == 0 && focusedPassedOne, "The requested test does not exist or does not pass:\n" + Truncate(focused.Output, 1500));
            Assert.True(full.ExitCode == 0, "The workspace's suite does not pass:\n" + Truncate(full.Output, 1500));
            Assert.NotEmpty(reviewRuns);
            Assert.False(reviewRuns.Last().BlocksCompletion, "Independent review blocked completion: " + reviewRuns.Last().FindingsJson);
            Assert.Empty(factoryAfter);
            Assert.Empty(factoryLate);
            Assert.Empty(foreignChanged);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            WriteEvidenceFile();
            TryDelete(tempDir);
        }
    }

    // ───────────────────────────── host ─────────────────────────────

    private static IHost BuildHost(string runtime, string model, string tempDir, string projectsBase, string dbPath, List<string> logs)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = FindWorkerProjectDirectory(),
            EnvironmentName = "Production"
        });

        builder.Logging.AddProvider(new CapturingLoggerProvider(logs));
        Program.ConfigureServices(builder);

        builder.Services.Remove(builder.Services.Single(d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>)));
        builder.Services.AddDbContext<FactoryDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

        var workspace = Instance<AgentWorkspaceOptions>(builder);
        workspace.ProjectsBaseDirectory = projectsBase;
        workspace.AllowedRoot = tempDir;

        var scheduler = Instance<TaskSchedulerOptions>(builder);
        scheduler.MaxConcurrentTasks = 1;

        // Shipped gateway: asserted, never overridden.
        var gateways = Instance<ModelGatewayOptions>(builder);
        Assert.Equal(GatewayKey, gateways.DefaultGateway);
        var gateway = gateways.Gateways[GatewayKey];
        Assert.True(gateway.Enabled);
        Assert.Equal(GatewayBaseUrl, gateway.BaseUrl);
        Assert.Equal(CredentialName, gateway.ApiKeySecretName);

        // One runtime for every agent type (implementation, repair and the independent review): no second runtime involved.
        var execution = Instance<ExecutionOptions>(builder);
        Assert.Equal(runtime, execution.Profiles[runtime].Adapter);
        Assert.Equal(GatewayKey, execution.Profiles[runtime].Gateway);
        execution.DefaultProfile = runtime;
        execution.AgentTypeProfiles.Clear();

        // Exactly one model in the catalog and in every category: index 1 (a fallback) can never exist.
        var routing = Instance<ModelRoutingOptions>(builder);
        routing.Models.Clear();
        routing.CategoryProfiles.Clear();
        routing.Models.Add(new ModelCapability
        {
            ModelId = model,
            Provider = "openrouter",
            ReasoningStrength = 4,
            MaxContextTokens = 32_000,
            CostTier = 1,
            IsCodingOptimized = true,
            IsAvailable = true
        });
        foreach (var category in RoutingCategories)
        {
            routing.CategoryProfiles[category] = new List<string> { model };
        }

        Assert.Empty(routing.Validate());

        if (runtime == "Hermes")
        {
            var hermesScratch = Path.Combine(tempDir, "hermes-scratch");
            Directory.CreateDirectory(hermesScratch);
            var hermes = Instance<HermesOptions>(builder);
            hermes.DockerImage = "nousresearch/hermes-agent:0.20.6";
            hermes.WorkspaceRoot = hermesScratch;
            hermes.NetworkMode = "bridge";
            hermes.Provider = "openrouter";
            hermes.ProviderApiKeyEnvironmentVariable = CredentialName;
            hermes.ProviderApiKeySecretName = CredentialName;
            hermes.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = GatewayBaseUrl;
            hermes.AdditionalEnvironmentVariables["HERMES_WRITE_SAFE_ROOT"] = "/workspace";
            hermes.DefaultModel = model;
        }
        else
        {
            var openHandsScratch = Path.Combine(tempDir, "openhands-scratch");
            Directory.CreateDirectory(openHandsScratch);
            var openHands = Instance<OpenHandsOptions>(builder);
            openHands.WorkspaceRoot = openHandsScratch;
            openHands.NetworkMode = "bridge";
            openHands.DefaultModel = model;
        }

        builder.Services.AddScoped<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddScoped<IProjectWorkspaceProvisioner, ProjectWorkspaceProvisioner>();
        builder.Services.AddScoped<IProjectSourceInitializer, AI.Factory.Infrastructure.Source.ProjectSourceInitializer>();
        builder.Services.AddScoped<ProjectService>();
        builder.Services.AddScoped<WorkItemService>();

        return builder.Build();
    }

    private static T Instance<T>(HostApplicationBuilder builder) where T : class =>
        (T)builder.Services.Single(d => d.ServiceType == typeof(T)).ImplementationInstance!;

    private async Task<(Guid ProjectId, Guid WorkItemId, string ProjectRoot)> CreateProjectAndWorkItemAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        await AgentDefinitionBootstrapper.EnsureBootstrapDefinitionsAsync(db);

        var projects = scope.ServiceProvider.GetRequiredService<ProjectService>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceResolver>();
        var workItems = scope.ServiceProvider.GetRequiredService<WorkItemService>();

        var project = await projects.CreateAsync("Phase P certification", "Disposable certification project");
        var projectRoot = resolver.Resolve(project).Path!;
        await projects.InitializeSourceAsync(project.Id, new SourceSpecification(ProjectSourceType.GitRepository, _bareRepo!, "main"));
        await projects.TransitionStatusAsync(project.Id, ProjectStatus.Specification);
        await projects.TransitionStatusAsync(project.Id, ProjectStatus.Architecture);
        await projects.TransitionStatusAsync(project.Id, ProjectStatus.Planning);

        var item = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, TaskTitle, TaskDescription, "Normal");
        item.AcceptanceCriteria = AcceptanceCriteria;
        await new WorkItemRepository(db).UpdateAsync(item);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        await projects.TransitionStatusAsync(project.Id, ProjectStatus.Implementation);

        return (project.Id, item.Id, projectRoot);
    }

    private string? _bareRepo;

    private async Task<WorkItem> DriveUntilSettledAsync(IHost host, Guid projectId, Guid workItemId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;

        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var item = await scope.ServiceProvider.GetRequiredService<IWorkItemRepository>().GetByIdAsync(workItemId);
                if (item is not null)
                {
                    if (item.Status != last)
                    {
                        last = item.Status;
                        Evidence($"[{DateTime.UtcNow:HH:mm:ss}] WorkItem -> {item.Status}");
                    }

                    if (Enum.TryParse<WorkItemStatus>(item.Status, out var parsed) && WorkItemTransitions.IsTerminal(parsed))
                    {
                        return item;
                    }
                }
            }

            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ITaskDispatchScheduler>().RunCycleAsync(projectId);
            }

            await Task.Delay(2000);
        }

        throw new TimeoutException($"WorkItem {workItemId} did not settle within {timeout}. Last observed: {last}.");
    }

    private async Task<(List<AgentRun> Runs, List<ReviewRun> ReviewRuns)> ReportPersistedEvidenceAsync(IHost host, Guid workItemId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

        var runs = await db.AgentRuns.Where(r => r.WorkItemId == workItemId).OrderBy(r => r.CreatedAtUtc).ToListAsync();
        foreach (var r in runs)
        {
            var (adapter, exitReason) = ReadMetadata(r.StructuredResult);
            var duration = r.StartedAtUtc is { } s && r.CompletedAtUtc is { } c ? $"{(c - s).TotalSeconds:F0}" : "-";
            Evidence(
                $"agentRun id={r.Id} attempt={r.Attempt} status={r.Status} provider={r.Provider ?? "-"} model={r.Model ?? "-"} " +
                $"adapter={adapter ?? "-"} exitReason={exitReason ?? "-"} failure={r.FailureCategory?.ToString() ?? "-"} durationS={duration} " +
                $"promptTokens={Reported(r.PromptTokens)} completionTokens={Reported(r.CompletionTokens)} totalTokens={Reported(r.TotalTokens)} " +
                $"error={Truncate(r.ErrorMessage)}");
            // Cost, runtime version, session/request ids exist only as structured-result metadata, and only when the
            // runtime reported them (Phase M); everything persisted is printed, nothing is derived.
            Evidence($"  metadata={Truncate(MetadataJson(r.StructuredResult), 1200)}");
            Evidence($"  output={Truncate(r.Output, 400)}");
        }

        foreach (var v in await db.ValidationRuns.Where(v => v.WorkItemId == workItemId).OrderBy(v => v.CreatedAtUtc).ToListAsync())
        {
            Evidence($"validationRun success={v.Success} build={v.BuildSucceeded} tests={v.TestsSucceeded?.ToString() ?? "-"}");
        }

        var reviewRuns = await db.ReviewRuns.Where(r => r.WorkItemId == workItemId).OrderBy(r => r.CreatedAtUtc).ToListAsync();
        foreach (var r in reviewRuns)
        {
            Evidence($"reviewRun blocksCompletion={r.BlocksCompletion} findings={r.FindingsJson}");
        }

        return (runs, reviewRuns);
    }

    private static string Reported(int? value) => value?.ToString() ?? "NOT REPORTED";

    private static string MetadataJson(string? structuredResult)
    {
        if (string.IsNullOrWhiteSpace(structuredResult))
        {
            return "NOT REPORTED";
        }

        using var document = JsonDocument.Parse(structuredResult);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "Metadata", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetRawText();
            }
        }

        return "NOT REPORTED";
    }

    // ───────────────────────────── fixture ─────────────────────────────

    private (string BareRepo, string SeedCommit, Dictionary<string, string> Hashes) CreateCertificationRepository(string tempDir)
    {
        var seed = Path.Combine(tempDir, "seed");
        Directory.CreateDirectory(Path.Combine(seed, "src"));
        Directory.CreateDirectory(Path.Combine(seed, "tests"));

        var files = new Dictionary<string, string>
        {
            [".gitignore"] = "bin/\nobj/\nTestResults/\n",
            ["Demo.slnx"] = "<Solution>\n  <Project Path=\"src/Demo.csproj\" />\n  <Project Path=\"tests/Demo.Tests.csproj\" />\n</Solution>\n",
            ["src/Demo.csproj"] =
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n",
            ["src/Calculator.cs"] =
                "namespace Demo;\n\npublic static class Calculator\n{\n    public static int Add(int a, int b) => a + b;\n}\n",
            ["tests/Demo.Tests.csproj"] =
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <IsPackable>false</IsPackable>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n" +
                "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.0.2\">\n      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>\n      <PrivateAssets>all</PrivateAssets>\n    </PackageReference>\n" +
                "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.11.1\" />\n    <ProjectReference Include=\"../src/Demo.csproj\" />\n  </ItemGroup>\n</Project>\n",
            ["tests/CalculatorTests.cs"] =
                "using Xunit;\n\nnamespace Demo.Tests;\n\npublic class CalculatorTests\n{\n    [Fact]\n    public void Add_ReturnsSum()\n    {\n        Assert.Equal(5, Calculator.Add(2, 3));\n    }\n}\n"
        };

        foreach (var (path, content) in files)
        {
            File.WriteAllText(Path.Combine(seed, path), content);
        }

        Git(seed, "init", "-b", "main");
        Git(seed, "config", "user.email", "certification@factory.local");
        Git(seed, "config", "user.name", "Certification");
        Git(seed, "config", "commit.gpgsign", "false");
        Git(seed, "config", "core.autocrlf", "false");
        Git(seed, "add", ".");
        Git(seed, "commit", "-m", "certification seed");
        var commit = Git(seed, "rev-parse", "HEAD").Trim();

        _bareRepo = Path.Combine(tempDir, "certification.git");
        Git(tempDir, "clone", "--bare", seed, _bareRepo);

        return (_bareRepo, commit, files.ToDictionary(f => f.Key, f => Sha256(f.Value)));
    }

    private static IReadOnlyList<string> ChangedFiles(string workspace, string seedCommit)
    {
        // core.quotepath=false: a non-ASCII name is reported as itself, not as git's octal-escaped form.
        var committed = Git(workspace, "-c", "core.quotepath=false", "diff", "--name-only", seedCommit, "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var working = Git(workspace, "-c", "core.quotepath=false", "status", "--porcelain", "--untracked-files=all")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Length > 3 ? l[3..].Trim().Trim('"') : l.Trim());
        return committed.Concat(working).Select(p => p.Replace('\\', '/')).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>A path with any non-printable-ASCII character is shown with its code points, so an invisible name stays visible.</summary>
    private static string DescribePath(string path) =>
        path.All(c => c is >= ' ' and <= '~')
            ? path
            : $"{path} (code points: {string.Join(" ", path.Select(c => $"U+{(int)c:X4}"))})";

    private static string DescribeFile(string root, string relativePath)
    {
        var full = Path.Combine(root, relativePath);
        if (Directory.Exists(full))
        {
            return $"{DescribePath(relativePath)} directory entries={Directory.EnumerateFileSystemEntries(full).Count()}";
        }

        if (!File.Exists(full))
        {
            return $"{DescribePath(relativePath)} (deleted or not a file)";
        }

        // Safe to preview: the credential scan above covers every workspace file and is asserted empty.
        var bytes = File.ReadAllBytes(full);
        var preview = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 160)).ReplaceLineEndings("\\n");
        return $"{DescribePath(relativePath)} bytes={bytes.Length} preview=\"{preview}\"";
    }

    private static List<string> FindForbiddenState(string workspace) =>
        Directory.EnumerateFileSystemEntries(workspace, "*", SearchOption.AllDirectories)
            .Where(p => ForbiddenRuntimeState.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(workspace, p))
            .ToList();

    private static List<string> FilesContaining(string root, string secret)
    {
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (new FileInfo(file).Length > 20_000_000)
                {
                    continue;
                }

                if (Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(secret, StringComparison.Ordinal))
                {
                    hits.Add(Path.GetRelativePath(root, file));
                }
            }
            catch (IOException)
            {
                // A locked build output cannot hold a value the Factory wrote; reported by count only.
            }
        }

        return hits;
    }

    // ───────────────────────────── docker / process ─────────────────────────────

    private sealed record Container(string Id, string Name, string State);

    private static List<Container> DockerContainers() =>
        Run(Directory.GetCurrentDirectory(), "docker", "ps", "-a", "--no-trunc", "--format", "{{.ID}}|{{.Names}}|{{.State}}")
            .Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|'))
            .Where(p => p.Length == 3)
            .Select(p => new Container(p[0], p[1], p[2]))
            .ToList();

    private static List<Container> FactoryContainers() =>
        DockerContainers().Where(c => c.Name.StartsWith(DockerContainerNaming.ContainerNamePrefix, StringComparison.Ordinal)).ToList();

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var result = Run(workingDirectory, "git", arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Output}");
        }

        return result.Output;
    }

    private static (int ExitCode, string Output) Run(string workingDirectory, string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, // git prints non-ASCII paths as UTF-8
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(600_000))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private readonly List<string> _evidence = new();

    private void Evidence(string line)
    {
        _evidence.Add(line);
        _output.WriteLine("CERT " + line);
    }

    private void WriteEvidenceFile()
    {
        var path = Environment.GetEnvironmentVariable("AI_FACTORY_CERT_EVIDENCE_FILE");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllLines(path, _evidence.Select(l => "CERT " + l).Append(string.Empty));
        }
    }

    private static (string? Adapter, string? ExitReason) ReadMetadata(string? structuredResult)
    {
        if (string.IsNullOrWhiteSpace(structuredResult))
        {
            return (null, null);
        }

        using var document = JsonDocument.Parse(structuredResult);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "Metadata", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
            {
                string? adapter = null, exitReason = null;
                foreach (var entry in property.Value.EnumerateObject())
                {
                    if (string.Equals(entry.Name, "ExecutionAdapter", StringComparison.OrdinalIgnoreCase))
                    {
                        adapter = entry.Value.ToString();
                    }
                    else if (string.Equals(entry.Name, "ExitReason", StringComparison.OrdinalIgnoreCase))
                    {
                        exitReason = entry.Value.ToString();
                    }
                }

                return (adapter, exitReason);
            }
        }

        return (null, null);
    }

    // Line endings normalized: the Project clone follows the host's core.autocrlf, the seed was written with LF.
    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n"))));

    private static string Truncate(string? text, int max = 300) =>
        string.IsNullOrEmpty(text) ? "-" : (text.Length <= max ? text : text[..max] + "…").ReplaceLineEndings(" ");

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal); // git objects are read-only
                }

                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported by the final temp-directory audit, never hidden as success.
        }
    }

    private static string FindWorkerProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("AI.Factory.slnx not found."), "src", "AI.Factory.Worker");
    }

    private sealed class CapturingLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var line = $"[{logLevel}] [{category}] {formatter(state, exception)} {exception}";
                lock (lines)
                {
                    lines.Add(line);
                }
            }
        }
    }
}
