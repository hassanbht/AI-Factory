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
/// Phase Q — End-to-End Benchmark &amp; Autonomous Software Delivery (Hermes only).
/// One explicit model (<c>AI_FACTORY_CERT_MODEL</c>) through the shipped OpenRouter
/// gateway, full Factory path: Project → WorkItem → scheduler → AgentRunWorker →
/// Hermes adapter → verification → acceptance → independent review → Completed,
/// then independent workspace/cleanup/credential audits.
///
/// Gated by the existing benchmark opt-ins (<c>AI_FACTORY_LIVE_BENCHMARK</c> or
/// <c>AI_FACTORY_RUN_BENCHMARK</c>) plus <c>OPENROUTER_API_KEY</c> and
/// <c>AI_FACTORY_CERT_MODEL</c>. Task selected by <c>AI_FACTORY_PHASE_Q_TASK</c>
/// (Q-A, Q-B, Q-C, Q-D). Evidence appended to
/// <c>AI_FACTORY_PHASE_Q_EVIDENCE_FILE</c> (fallback <c>AI_FACTORY_CERT_EVIDENCE_FILE</c>).
/// Any <c>auto</c> model id is rejected fail-closed. No fallback runtime.
/// </summary>
[Collection(TestCollections.DockerAndBuilds)]
public sealed class PhaseQBenchmarkTests
{
    private const string GatewayKey = "OpenRouter";
    private const string GatewayBaseUrl = "https://openrouter.ai/api/v1";
    private const string CredentialName = "OPENROUTER_API_KEY";
    private const string ModelEnv = "AI_FACTORY_CERT_MODEL";
    private const string TaskEnv = "AI_FACTORY_PHASE_Q_TASK";

    private static readonly string[] RoutingCategories =
    {
        "requirements-analysis", "architecture", "planning", "coding", "debugging", "testing", "code-review", "documentation"
    };

    private static readonly string[] ForbiddenRuntimeState = { "conversations", "bash_events", "base_state.json", ".hermes", ".openhands" };

    private readonly ITestOutputHelper _output;

    public PhaseQBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "PhaseQ.LiveExecutionSafety")]
    public Task Hermes_PhaseQ_Run_ThroughFactoryPath_RemainsDisabledUntilSeparatelyAuthorizedCanary() =>
        Assert.ThrowsAsync<InvalidOperationException>(() => RunPhaseQAsync("Hermes"));

    private async Task RunPhaseQAsync(string runtime)
    {
        // Q.3 must not execute billed requests regardless of ambient opt-ins or
        // credentials. A separately authorized Q.4 change may replace this
        // unconditional interlock after security and capture gates are cleared.
        if (!IsPhaseQLiveExecutionAuthorized())
        {
            throw new InvalidOperationException("Phase Q live benchmark execution is disabled in Q.3; no provider request was made.");
        }

        Assert.Equal("Hermes", runtime);

        var model = Environment.GetEnvironmentVariable(ModelEnv)!.Trim();
        var credential = Environment.GetEnvironmentVariable(CredentialName)!;
        var taskKey = (Environment.GetEnvironmentVariable(TaskEnv) ?? string.Empty).Trim();

        Assert.False(
            model.StartsWith("auto", StringComparison.OrdinalIgnoreCase)
                || model.Contains("/auto", StringComparison.OrdinalIgnoreCase),
            $"'{model}' is an automatic model id; Phase Q runs explicit models only.");
        Assert.True(PhaseQTasks.AllKeys().Contains(taskKey),
            $"{TaskEnv} must be one of Q-A, Q-B, Q-C, Q-D (was '{taskKey}').");

        var spec = PhaseQTasks.Get(taskKey);

        var started = DateTime.UtcNow;
        Evidence($"phase=Q task={spec.TaskKey} category={spec.Category} runtime={runtime} model={model} gateway={GatewayKey} baseUrl={GatewayBaseUrl} credential={CredentialName} (value never printed) startUtc={started:O}");

        var before = DockerContainers();
        var factoryBefore = before.Where(c => c.Name.StartsWith(DockerContainerNaming.ContainerNamePrefix, StringComparison.Ordinal)).ToList();
        var foreignBefore = before.Where(c => !c.Name.StartsWith(DockerContainerNaming.ContainerNamePrefix, StringComparison.Ordinal)).ToList();
        Evidence($"containers.before factoryOwned={factoryBefore.Count} foreign={foreignBefore.Count}");
        Assert.Empty(factoryBefore);

        var tempDir = Path.Combine(Path.GetTempPath(), $"aif-phaseq-{taskKey.ToLowerInvariant()}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(tempDir);
        var logs = new List<string>();

        try
        {
            var (bareRepo, seedCommit, seedHashes) = CreateTaskRepository(tempDir, spec);
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

            var (projectId, workItemId, projectRoot) = await CreateProjectAndWorkItemAsync(host, spec, bareRepo);
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

            var changed = ChangedFiles(projectRoot, seedCommit);
            Evidence($"workspace.changedFiles=[{string.Join(",", changed.Select(DescribePath))}]");
            var classifications = changed.ToDictionary(
                p => p,
                p => spec.Benchmark.FileClassifier?.Invoke(p) ?? "Unexpected",
                StringComparer.OrdinalIgnoreCase);
            foreach (var kv in classifications)
            {
                Evidence($"workspace.classify {DescribePath(kv.Key)}={kv.Value}");
            }
            var unexpected = classifications.Where(kv => kv.Value == "Unexpected").Select(kv => kv.Key).ToList();
            foreach (var u in unexpected)
            {
                Evidence("workspace.unexpected " + DescribeFile(projectRoot, u));
            }
            var missingRequired = spec.RequiredFiles
                .Where(r => !changed.Contains(r, StringComparer.OrdinalIgnoreCase))
                .ToList();
            Evidence($"workspace.missingRequired=[{string.Join(",", missingRequired)}]");
            var forbidden = FindForbiddenState(projectRoot);
            Evidence($"workspace.forbiddenRuntimeState=[{string.Join(",", forbidden)}]");
            var credentialFiles = FilesContaining(projectRoot, credential);
            Evidence($"workspace.filesContainingCredential={credentialFiles.Count}");
            var credentialLogLines = logs.Count(l => l.Contains(credential, StringComparison.Ordinal));
            Evidence($"logs.lines={logs.Count} linesContainingCredential={credentialLogLines}");
            var fallbackLines = logs.Where(l => l.Contains("fallback", StringComparison.OrdinalIgnoreCase)).ToList();
            Evidence($"logs.fallbackMentions={fallbackLines.Count}");

            foreach (var kv in spec.RequiredTextChecks)
            {
                var artifactPath = Path.Combine(projectRoot, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                var contains = File.Exists(artifactPath) && File.ReadAllText(artifactPath).Contains(kv.Value, StringComparison.Ordinal);
                Evidence($"artifact.check path={kv.Key} value={kv.Value} present={contains}");
            }

            var focusedParts = spec.FocusedTestFilter.Split('~');
            var focused = Run(projectRoot, "dotnet", "test", "--filter", spec.FocusedTestFilter);
            var focusedPassedOne = Regex.IsMatch(focused.Output, @"Passed:\s+1\b") && Regex.IsMatch(focused.Output, @"Failed:\s+0\b");
            var suite = Run(projectRoot, "dotnet", "test");
            Evidence($"independent.focusedTest exit={focused.ExitCode} passedExactlyOne={focusedPassedOne}; fullSuite exit={suite.ExitCode} {Regex.Match(suite.Output, @"(Passed|Failed)!.*").Value.Trim()}");

            Assert.True(finalItem.Status == WorkItemStatus.Completed.ToString(),
                $"WorkItem ended {finalItem.Status}, not Completed. Error: {finalItem.CurrentError}");
            Assert.NotEmpty(runs);
            Assert.All(runs, r => Assert.Equal(model, r.Model));
            Assert.Contains(runs, r => r.Status == AgentRunStatus.Succeeded && ReadMetadata(r.StructuredResult).Adapter == runtime);
            Assert.Empty(missingRequired);
            Assert.Empty(unexpected);
            Assert.Empty(forbidden);
            Assert.Empty(credentialFiles);
            Assert.Equal(0, credentialLogLines);
            foreach (var kv in spec.RequiredTextChecks)
            {
                var full2 = Path.Combine(projectRoot, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(full2), $"Required artifact missing: {kv.Key}");
                Assert.Contains(kv.Value, File.ReadAllText(full2), StringComparison.Ordinal);
            }
            Assert.True(focused.ExitCode == 0 && focusedPassedOne, "The requested test does not exist or does not pass:\n" + Truncate(focused.Output, 1500));
            Assert.True(suite.ExitCode == 0, "The workspace's suite does not pass:\n" + Truncate(suite.Output, 1500));
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

    private static bool IsPhaseQLiveExecutionAuthorized() => false;

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

        var gateways = Instance<ModelGatewayOptions>(builder);
        Assert.Equal(GatewayKey, gateways.DefaultGateway);
        var gateway = gateways.Gateways[GatewayKey];
        Assert.True(gateway.Enabled);
        Assert.Equal(GatewayBaseUrl, gateway.BaseUrl);
        Assert.Equal(CredentialName, gateway.ApiKeySecretName);

        var execution = Instance<ExecutionOptions>(builder);
        Assert.Equal(runtime, execution.Profiles[runtime].Adapter);
        Assert.Equal(GatewayKey, execution.Profiles[runtime].Gateway);
        execution.DefaultProfile = runtime;
        execution.AgentTypeProfiles.Clear();

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

        builder.Services.AddScoped<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddScoped<IProjectWorkspaceProvisioner, ProjectWorkspaceProvisioner>();
        builder.Services.AddScoped<IProjectSourceInitializer, AI.Factory.Infrastructure.Source.ProjectSourceInitializer>();
        builder.Services.AddScoped<ProjectService>();
        builder.Services.AddScoped<WorkItemService>();

        return builder.Build();
    }

    private static T Instance<T>(HostApplicationBuilder builder) where T : class =>
        (T)builder.Services.Single(d => d.ServiceType == typeof(T)).ImplementationInstance!;

    private async Task<(Guid ProjectId, Guid WorkItemId, string ProjectRoot)> CreateProjectAndWorkItemAsync(IHost host, PhaseQTaskSpec spec, string bareRepo)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        await AgentDefinitionBootstrapper.EnsureBootstrapDefinitionsAsync(db);

        var projects = scope.ServiceProvider.GetRequiredService<ProjectService>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceResolver>();
        var workItems = scope.ServiceProvider.GetRequiredService<WorkItemService>();

        var project = await projects.CreateAsync($"Phase Q {spec.TaskKey}", "Disposable Phase Q benchmark project");
        var projectRoot = resolver.Resolve(project).Path!;
        await projects.InitializeSourceAsync(project.Id, new SourceSpecification(ProjectSourceType.GitRepository, bareRepo, "main"));
        await projects.TransitionStatusAsync(project.Id, ProjectStatus.Specification);
        await projects.TransitionStatusAsync(project.Id, ProjectStatus.Architecture);
        await projects.TransitionStatusAsync(project.Id, ProjectStatus.Planning);

        var item = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, spec.Benchmark.Title, spec.Benchmark.Description, "Normal");
        item.AcceptanceCriteria = spec.AcceptanceCriteriaJson;
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

    private (string BareRepo, string SeedCommit, Dictionary<string, string> Hashes) CreateTaskRepository(string tempDir, PhaseQTaskSpec spec)
    {
        var seed = Path.Combine(tempDir, "seed");
        Directory.CreateDirectory(seed);

        spec.Benchmark.SetupBaselineAction?.Invoke(seed);

        var files = Directory.EnumerateFiles(seed, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(seed, f).Replace('\\', '/'),
                f => Sha256(File.ReadAllText(f)));

        Git(seed, "init", "-b", "main");
        Git(seed, "config", "user.email", "phaseq@factory.local");
        Git(seed, "config", "user.name", "PhaseQ");
        Git(seed, "config", "commit.gpgsign", "false");
        Git(seed, "config", "core.autocrlf", "false");
        Git(seed, "add", ".");
        Git(seed, "commit", "-m", $"phase q seed {spec.TaskKey}");
        var commit = Git(seed, "rev-parse", "HEAD").Trim();

        _bareRepo = Path.Combine(tempDir, "phaseq.git");
        Git(tempDir, "clone", "--bare", seed, _bareRepo);

        return (_bareRepo, commit, files);
    }

    private static IReadOnlyList<string> ChangedFiles(string workspace, string seedCommit)
    {
        var committed = Git(workspace, "-c", "core.quotepath=false", "diff", "--name-only", seedCommit, "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var working = Git(workspace, "-c", "core.quotepath=false", "status", "--porcelain", "--untracked-files=all")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Length > 3 ? l[3..].Trim().Trim('"') : l.Trim());
        return committed.Concat(working).Select(p => p.Replace('\\', '/')).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

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
            StandardOutputEncoding = Encoding.UTF8,
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
        _output.WriteLine("PHASEQ " + line);
    }

    private void WriteEvidenceFile()
    {
        var path = Environment.GetEnvironmentVariable("AI_FACTORY_PHASE_Q_EVIDENCE_FILE")
            ?? Environment.GetEnvironmentVariable("AI_FACTORY_CERT_EVIDENCE_FILE");
        if (!string.IsNullOrWhiteSpace(path))
        {
            // The test host runs with CWD under bin/; anchor relative evidence
            // paths at the solution root so they land in the repo TestResults/.
            if (!Path.IsPathRooted(path))
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
                {
                    dir = dir.Parent;
                }
                if (dir is not null)
                {
                    path = Path.Combine(dir.FullName, path);
                }
            }
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }
            File.AppendAllLines(path, _evidence.Select(l => "PHASEQ " + l).Append(string.Empty));
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
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
