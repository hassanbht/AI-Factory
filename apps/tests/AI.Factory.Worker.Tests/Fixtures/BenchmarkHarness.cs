using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using AI.Factory.Infrastructure.Source;
using AI.Factory.Infrastructure.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace AI.Factory.Worker.Tests.Fixtures;

/// <summary>
/// Phase F — Benchmark Execution Harness.
/// Manages canonical baseline creation, twin isolated workspace setup,
/// headless AgentRun dispatch, Factory verification gate execution,
/// changed file diff capture, and container cleanup validation.
/// </summary>
public static class BenchmarkHarness
{
    public const string BenchmarkId = "BENCH-001";
    public const string ControlledModel = "deepseek/deepseek-v4-flash-0731";
    public const string ControlledGateway = "https://openrouter.ai/api/v1";

    public const string TaskTitle = "Implement Multiply in Calculator with Unit Tests and Update Notes";
    public const string TaskDescription =
        "In src/Calculator.cs, add a Multiply method: `public int Multiply(int a, int b) => a * b;`.\n" +
        "In tests/CalculatorTests.cs, add a unit test `Multiply_ReturnsProduct` verifying `new Calculator().Multiply(3, 4) == 12`.\n" +
        "In NOTES.md, append `- Multiply` to the Features list.\n" +
        "Do not modify any other file.";


    public static void TryLoadEnvLive()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                var candidate = Path.Combine(dir, ".env.live");
                if (File.Exists(candidate))
                {
                    foreach (var line in File.ReadAllLines(candidate))
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
                        var idx = trimmed.IndexOf('=');
                        if (idx > 0)
                        {
                            var key = trimmed[..idx].Trim();
                            var val = trimmed[(idx + 1)..].Trim();
                            if (key.Contains("SMOKE_TEST", StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                            {
                                Environment.SetEnvironmentVariable(key, val);
                            }
                        }
                    }
                    break;
                }
                var parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }
        }
        catch { }
    }

    public static (string BareRepoPath, string CommitSha, string BaselineHash) CreateCanonicalBaselineBareRepo(
        string tempDir,
        BenchmarkTaskDefinition? task = null)
    {
        task ??= BenchmarkTaskDefinition.TaskB_MultiFile();
        var seedDir = Path.Combine(tempDir, "baseline-seed");
        Directory.CreateDirectory(seedDir);

        if (task.SetupBaselineAction is not null)
        {
            task.SetupBaselineAction(seedDir);
        }
        else
        {
            BenchmarkTaskDefinition.TaskB_MultiFile().SetupBaselineAction!(seedDir);
        }

        RunGit(seedDir, "init", "-b", "main");
        RunGit(seedDir, "config", "user.email", "benchmark@ai-factory.local");
        RunGit(seedDir, "config", "user.name", "BenchmarkRunner");
        RunGit(seedDir, "config", "commit.gpgsign", "false");
        RunGit(seedDir, "add", ".");
        RunGit(seedDir, "commit", "-m", $"Canonical benchmark baseline for {task.TaskId}");

        var commitSha = RunGitWithOutput(seedDir, "rev-parse", "HEAD");
        var baselineHash = ComputeDirectoryHash(seedDir);

        var bareRepoPath = Path.Combine(tempDir, "canonical-demo.git");
        RunGit(tempDir, "clone", "--bare", seedDir, bareRepoPath);

        return (bareRepoPath, commitSha, baselineHash);
    }

    public static async Task<BenchmarkRunRecord> ExecuteIterationAsync(
        string adapter,
        int runIndex,
        string bareRepoPath,
        string commitSha,
        string baselineHash,
        string model,
        string? openRouterApiKey = null,
        ITestOutputHelper? output = null,
        BenchmarkTaskDefinition? task = null,
        string? gateway = null,
        string? gatewayBaseUrl = null)
    {
        task ??= BenchmarkTaskDefinition.TaskB_MultiFile();
        var iterationTemp = Path.Combine(Path.GetTempPath(), $"aif-bench-{adapter.ToLowerInvariant()}-{runIndex}-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(iterationTemp);

        var projectsBase = Path.Combine(iterationTemp, "projects");
        Directory.CreateDirectory(projectsBase);

        var workspaceOptions = new AgentWorkspaceOptions
        {
            ProjectsBaseDirectory = projectsBase,
            AllowedRoot = iterationTemp
        };
        var resolver = new ProjectWorkspaceResolver(workspaceOptions);
        var commandPolicyOptions = new CommandPolicyOptions();
        var executionLimitsOptions = new ExecutionLimitsOptions { BuildTimeoutMinutes = 5 };

        var preContainerCount = GetFactoryContainerCount();
        if (preContainerCount > 0)
        {
            output?.WriteLine($"[WARNING] Pre-execution Factory-owned container count is {preContainerCount}, expected 0.");
        }

        using var testHost = BuildHost(adapter, iterationTemp, projectsBase, model, openRouterApiKey, gateway, gatewayBaseUrl);

        Guid projectId, workItemId, agentRunId;
        string resolvedProjectRoot;

        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

            var provisioner = new ProjectWorkspaceProvisioner(
                resolver, workspaceOptions, NullLogger<ProjectWorkspaceProvisioner>.Instance);
            var sourceInitializer = new ProjectSourceInitializer(
                resolver, new SystemProcessRunner(commandPolicyOptions), executionLimitsOptions, NullLogger<ProjectSourceInitializer>.Instance);
            var projectService = new ProjectService(new ProjectRepository(db), provisioner, sourceInitializer: sourceInitializer);

            var project = await projectService.CreateAsync($"Benchmark {adapter} {task.TaskId} Run {runIndex}", "Phase G benchmark run");
            projectId = project.Id;
            resolvedProjectRoot = resolver.Resolve(project).Path!;

            await projectService.InitializeSourceAsync(
                projectId, new SourceSpecification(ProjectSourceType.GitRepository, bareRepoPath));

            await new AgentDefinitionRepository(db).CreateAsync(
                new AgentDefinition { Name = "Coder", AgentType = "Implementation" });

            var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
            var item = await workItems.CreateAsync(
                projectId, WorkItemType.Implementation, task.Title, task.Description, "Normal");
            workItemId = item.Id;
            await workItems.TransitionStatusAsync(workItemId, WorkItemStatus.Ready);

            var scheduler = scope.ServiceProvider.GetRequiredService<ITaskDispatchScheduler>();
            var cycle = await scheduler.RunCycleAsync(projectId);
            agentRunId = cycle.Outcomes.Single().AgentRunId!.Value;

            var runRepo = new AgentRunRepository(db);
            var reloaded = (await runRepo.GetByIdAsync(agentRunId))!;
            reloaded.Model = model;
            await runRepo.UpdateAsync(reloaded);
        }

        var startedAtUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var worker = (AI.Factory.Worker.AgentRunWorker)testHost.Host.Services
            .GetServices<IHostedService>()
            .Single(s => s is AI.Factory.Worker.AgentRunWorker);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var finalRun = await WaitForTerminalAsync(testHost, agentRunId, TimeSpan.FromSeconds(300));
        sw.Stop();
        var completedAtUtc = DateTime.UtcNow;

        await worker.StopAsync(CancellationToken.None);

        var verificationSw = Stopwatch.StartNew();
        VerificationResult? verification = null;
        string? workItemStatus = null;
        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var gate = scope.ServiceProvider.GetRequiredService<IVerificationGate>();
            var project = (await new ProjectRepository(db).GetByIdAsync(projectId))!;
            var workItem = (await new WorkItemRepository(db).GetByIdAsync(workItemId))!;
            // The Factory's own decision (AgentRunWorker -> AgentRunWorkItemSync), captured
            // before the harness runs its independent verification below.
            workItemStatus = workItem.Status;
            verification = await gate.VerifyAsync(project, workItem, CancellationToken.None);
        }
        verificationSw.Stop();

        // Workspace inspection
        var statusOutput = RunGitWithOutput(resolvedProjectRoot, "status", "--porcelain");
        var diffOutput = RunGitWithOutput(resolvedProjectRoot, "diff");

        var changedFiles = new List<string>();
        var classifications = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = line.Substring(3).Trim();
            changedFiles.Add(path);

            if (task.FileClassifier is not null)
            {
                classifications[path] = task.FileClassifier(path);
            }
            else if (path.Equals("src/Calculator.cs", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("tests/CalculatorTests.cs", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("NOTES.md", StringComparison.OrdinalIgnoreCase))
            {
                classifications[path] = "Required";
            }
            else if (path.Contains("obj/") || path.Contains("bin/") || path.Contains(".git/"))
            {
                classifications[path] = "AcceptableIncidental";
            }
            else
            {
                classifications[path] = "Unexpected";
            }
        }

        // Post-run container cleanup assertion
        var postContainerCount = GetFactoryContainerCount();
        var cleanupVerified = postContainerCount == 0;

        int artifactCount = 0;
        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            artifactCount = await db.Artifacts.CountAsync(a => a.ProjectId == projectId);
        }

        var (adapterMeta, exitReason) = ReadAdapterMetadata(finalRun?.StructuredResult);
        // Phase J — token usage is persisted on the AgentRun columns; the structured-result
        // metadata has never carried it, so the metadata lookup alone always reported null.
        var (metaInput, metaOutput, metaTotal) = ExtractTokens(finalRun?.StructuredResult);
        var inputTokens = finalRun?.PromptTokens ?? metaInput;
        var outputTokens = finalRun?.CompletionTokens ?? metaOutput;
        var totalTokens = finalRun?.TotalTokens ?? metaTotal;

        var record = new BenchmarkRunRecord
        {
            BenchmarkId = task.TaskId,
            TaskCategory = task.Category,
            TaskTitle = task.Title,
            Runtime = adapter,
            RunId = agentRunId,
            WorkItemId = workItemId,
            ProjectId = projectId,
            SourceCommit = commitSha,
            BaselineHash = baselineHash,
            Model = model,
            Gateway = ControlledGateway,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            ExecutionDurationSeconds = sw.Elapsed.TotalSeconds,
            TerminalStatus = finalRun?.Status.ToString() ?? "Unknown",
            FailureCategory = finalRun?.FailureCategory?.ToString(),
            ExitReason = exitReason,
            AttemptNumber = finalRun?.Attempt ?? 1,
            RetryCount = 0,
            RepairCount = 0,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalTokens = totalTokens,
            AcceptanceStatus = (verification?.Status == VerificationStatus.Passed) ? "Passed" : "Failed",
            VerificationDurationSeconds = verificationSw.Elapsed.TotalSeconds,
            ReviewStatus = "Unchanged",
            ChangedFiles = changedFiles,
            FileClassifications = classifications,
            ArtifactCount = artifactCount,
            CleanupVerified = cleanupVerified,
            DiffSummary = Truncate(diffOutput, 500),
            Notes = $"Verification Summary: {verification?.Summary}",
            ErrorMessage = finalRun?.ErrorMessage,
            GatewayFailure = ReadMetadata(finalRun?.StructuredResult, "GatewayFailure"),
            WorkItemStatus = workItemStatus
        };

        try { Directory.Delete(iterationTemp, recursive: true); } catch { }

        return record;
    }

    private static (string? Adapter, string? ExitReason) ReadAdapterMetadata(string? structuredResult)
    {
        if (string.IsNullOrWhiteSpace(structuredResult)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(structuredResult);
            if (!doc.RootElement.TryGetProperty("Metadata", out var meta)) return (null, null);
            meta.TryGetProperty("ExecutionAdapter", out var a);
            meta.TryGetProperty("ExitReason", out var e);
            return (a.GetString(), e.GetString());
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? ReadMetadata(string? structuredResult, string key)
    {
        if (string.IsNullOrWhiteSpace(structuredResult)) return null;
        try
        {
            using var doc = JsonDocument.Parse(structuredResult);
            return doc.RootElement.TryGetProperty("Metadata", out var meta)
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (long? Input, long? Output, long? Total) ExtractTokens(string? structuredResult)
    {
        if (string.IsNullOrWhiteSpace(structuredResult)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(structuredResult);
            if (!doc.RootElement.TryGetProperty("Metadata", out var meta)) return (null, null, null);

            long? input = null, output = null, total = null;
            if (meta.TryGetProperty("InputTokens", out var inEl) && inEl.TryGetInt64(out var inVal)) input = inVal;
            if (meta.TryGetProperty("OutputTokens", out var outEl) && outEl.TryGetInt64(out var outVal)) output = outVal;
            if (meta.TryGetProperty("TotalTokens", out var totEl) && totEl.TryGetInt64(out var totVal)) total = totVal;

            return (input, output, total);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static async Task<AgentRun?> WaitForTerminalAsync(BenchmarkTestHost testHost, Guid runId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = testHost.Host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var run = await new AgentRunRepository(db).GetByIdAsync(runId);
            if (run is not null && AgentRunTransitions.IsTerminal(run.Status))
            {
                return run;
            }
            await Task.Delay(500);
        }
        return null;
    }

    private static BenchmarkTestHost BuildHost(
        string adapter,
        string tempDir,
        string projectsBase,
        string model,
        string? openRouterApiKey,
        string? gateway = null,
        string? gatewayBaseUrl = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = FindWorkerProjectDirectory(),
            EnvironmentName = "Production"
        });

        Program.ConfigureServices(builder);

        var dbDescriptor = builder.Services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));
        if (dbDescriptor is not null) builder.Services.Remove(dbDescriptor);
        builder.Services.AddDbContext<FactoryDbContext>(options => options.UseSqlite(connection));

        var workspaceOptions = (AgentWorkspaceOptions)builder.Services
            .Single(d => d.ServiceType == typeof(AgentWorkspaceOptions)).ImplementationInstance!;
        workspaceOptions.ProjectsBaseDirectory = projectsBase;
        workspaceOptions.AllowedRoot = tempDir;

        var executionOptions = (ExecutionOptions)builder.Services
            .Single(d => d.ServiceType == typeof(ExecutionOptions)).ImplementationInstance!;
        executionOptions.Profiles[adapter] = new ExecutionProfileOptions { Adapter = adapter, Gateway = gateway };

        // Phase K — an explicitly routed run says where it goes on the gateway itself (enabled, its
        // endpoint); the runtime is never pointed elsewhere behind the route's back.
        if (gateway is not null)
        {
            var gateways = (ModelGatewayOptions)builder.Services
                .Single(d => d.ServiceType == typeof(ModelGatewayOptions)).ImplementationInstance!;
            gateways.Gateways[gateway].Enabled = true;
            if (gatewayBaseUrl is not null)
            {
                gateways.Gateways[gateway].BaseUrl = gatewayBaseUrl;
            }
        }
        executionOptions.DefaultProfile = adapter;
        executionOptions.AgentTypeProfiles["Implementation"] = adapter;

        var workerOptions = new AgentWorkerOptions
        {
            PollingInterval = TimeSpan.FromMilliseconds(200),
            ExecutionTimeout = TimeSpan.FromSeconds(300)
        };
        var workerDesc = builder.Services.Single(d => d.ServiceType == typeof(AgentWorkerOptions));
        builder.Services.Remove(workerDesc);
        builder.Services.AddSingleton(workerOptions);

        if (adapter.Equals("Hermes", StringComparison.OrdinalIgnoreCase))
        {
            var hermes = (HermesOptions)builder.Services
                .Single(d => d.ServiceType == typeof(HermesOptions)).ImplementationInstance!;
            hermes.NetworkMode = "bridge";
            hermes.Provider = "openrouter";
            hermes.ProviderApiKeySecretName = "OPENROUTER_API_KEY";
            hermes.ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY";
            hermes.DefaultModel = model;
            hermes.TimeoutSeconds = 300;
        }
        else
        {
            var openHands = (OpenHandsOptions)builder.Services
                .Single(d => d.ServiceType == typeof(OpenHandsOptions)).ImplementationInstance!;
            openHands.NetworkMode = "bridge";
            // Phase K — endpoint (ControlledGateway) and OPENROUTER_API_KEY come from the default
            // OpenRouter gateway the profile resolves to, not from OpenHands options.
            openHands.DefaultModel = model;
            openHands.WorkspaceRoot = Path.Combine(tempDir, "oh-ws");
        }

        var host = builder.Build();

        var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(connection).Options;
        using (var db = new FactoryDbContext(schemaOptions))
        {
            db.Database.EnsureCreated();
        }

        return new BenchmarkTestHost(host, connection);
    }

    public static int GetFactoryContainerCount()
    {
        try
        {
            var startInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("ps");
            startInfo.ArgumentList.Add("-a");
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add("label=factory=ai-factory");
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add("{{.ID}}");
            using var process = Process.Start(startInfo)!;
            process.WaitForExit(10000);
            var output = process.StandardOutput.ReadToEnd();
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["GIT_AUTHOR_DATE"] = "2026-09-24T00:00:00Z";
        startInfo.Environment["GIT_COMMITTER_DATE"] = "2026-09-24T00:00:00Z";
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit(30000);
        if (process.ExitCode != 0)
        {
            var err = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {err}");
        }
    }

    private static string RunGitWithOutput(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["GIT_AUTHOR_DATE"] = "2026-09-24T00:00:00Z";
        startInfo.Environment["GIT_COMMITTER_DATE"] = "2026-09-24T00:00:00Z";
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit(30000);
        return process.StandardOutput.ReadToEnd().Trim();
    }

    private static string ComputeDirectoryHash(string dir)
    {
        using var sha = SHA256.Create();
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".git"))
            .OrderBy(f => f)
            .ToList();

        var sb = new StringBuilder();
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(dir, f);
            var bytes = File.ReadAllBytes(f);
            var hash = Convert.ToHexString(sha.ComputeHash(bytes));
            sb.Append($"{rel}:{hash};");
        }
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value.Substring(0, max) + "...";
    }

    private static string FindWorkerProjectDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "apps", "src", "AI.Factory.Worker");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }
        throw new InvalidOperationException("Could not locate apps/src/AI.Factory.Worker.");
    }
}

public sealed class BenchmarkTestHost : IDisposable
{
    public IHost Host { get; }
    public SqliteConnection Connection { get; }

    public BenchmarkTestHost(IHost host, SqliteConnection connection)
    {
        Host = host;
        Connection = connection;
    }

    public void Dispose()
    {
        Host.Dispose();
        Connection.Dispose();
    }
}
