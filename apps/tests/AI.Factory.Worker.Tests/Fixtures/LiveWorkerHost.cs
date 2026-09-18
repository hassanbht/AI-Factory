using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AI.Factory.Worker.Tests.Fixtures;

/// <summary>
/// The real Worker composition root, wired for live execution (real Docker,
/// real Hermes, real provider) against an isolated per-test SQLite file — no
/// shared in-memory connection, WAL enabled, pools cleared on dispose — and an
/// isolated temp workspace.
///
/// Introduced in Phase 18.20 inside <c>LiveProjectLifecycleE2ETests</c> and
/// lifted here in Phase 18.23 so the live Planner tests run against exactly the
/// same host rather than a second, subtly different copy of it. Nothing about
/// the wiring changed in the move: <see cref="Program.ConfigureServices"/> still
/// builds everything, and only the workspace, database, network and provider
/// routing are test-specific. TimeoutSeconds/ExecutionTimeout stay at the
/// shipped production defaults (Phase 18.23 §14).
/// </summary>
public sealed class LiveWorkerHost : IDisposable
{
    private readonly string _tempDir;

    private readonly string _dbPath;

    private LiveWorkerHost(
        IHost host, string tempDir, string bareRepoPath, string projectsBaseDirectory, string dbPath)
    {
        Host = host;
        _tempDir = tempDir;
        _dbPath = dbPath;
        BareRepoPath = bareRepoPath;
        ProjectsBaseDirectory = projectsBaseDirectory;
    }

    /// <summary>
    /// Phase 18.26 §5/§6 — a second, independent Worker composition root over
    /// the SAME database and workspace: the in-process equivalent of
    /// restarting the Worker process. Nothing is shared with
    /// <see cref="Host"/> except the persisted state, which is exactly the
    /// property the recovery test has to exercise. The caller owns and
    /// disposes the returned host.
    /// </summary>
    public IHost BuildRestartedWorkerHost() => BuildHost(_tempDir, ProjectsBaseDirectory, _dbPath);

    public IHost Host { get; }

    /// <summary>A real bare Git repository, cloneable as a Project source.</summary>
    public string BareRepoPath { get; }

    public string ProjectsBaseDirectory { get; }

    /// <summary>
    /// Phase 18.22 §10/§23 — provider-neutral live configuration. Nothing here
    /// is hard-coded to a vendor: the base URL and each role's model come from
    /// the environment. The default is the OmniRoute gateway this repository's
    /// real-Hermes tests have used since Phase 18.1.7. Point these at anything
    /// else OpenAI-compatible and the same code path runs unchanged.
    /// </summary>
    public static string LiveBaseUrl =>
        Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_BASE_URL")
        ?? "http://host.docker.internal:20128/v1";

    public static string LiveModel =>
        Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_MODEL") ?? "auto/coding:free";

    /// <summary>
    /// Phase 18.24 §3/§17 — the NAME of the secret the provider key lives under,
    /// never the key itself. A provider switch already needed only environment
    /// changes for the base URL and the model; leaving the secret name pinned to
    /// one vendor's spelling meant every other provider's key had to be aliased
    /// under a misleading variable. The default is unchanged.
    /// </summary>
    public static string LiveApiKeySecretName =>
        Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_API_KEY_SECRET") ?? "OMNIROUTE_API_KEY";

    /// <summary>Each role may be pointed at its own model; by default they share one.</summary>
    public static string ModelForRole(string role) =>
        Environment.GetEnvironmentVariable($"AI_FACTORY_LIVE_{role.ToUpperInvariant()}_MODEL") ?? LiveModel;

    /// <summary>
    /// Phase 18.26 §10/§11 — the role's FALLBACK model, used only when the
    /// primary fails for an infrastructure/provider/model-availability reason
    /// (AgentOrchestrator.IsFallbackEligible). Never used to re-run a failed
    /// test or a failed acceptance contract.
    ///
    /// The defaults are the §11 configuration HYPOTHESIS, not an accepted
    /// production fact: reasoning/structured-output roles fall back to the
    /// Auto Router (which charges at whatever model it routes to), and coding
    /// roles fall back to a cheap coding-specialised model. Laguna advertises
    /// no response_format, which is exactly why it is a coding fallback and
    /// never a Planner primary. Set the variable to an empty string to give a
    /// role no fallback at all.
    /// </summary>
    public static string? FallbackModelForRole(string role)
    {
        var configured = Environment.GetEnvironmentVariable(
            $"AI_FACTORY_LIVE_{role.ToUpperInvariant()}_FALLBACK_MODEL");

        if (configured is not null)
        {
            return string.IsNullOrWhiteSpace(configured) ? null : configured;
        }

        return role.ToLowerInvariant() switch
        {
            "planner" or "review" => "openrouter/auto-beta",
            _ => "poolside/laguna-s-2.1"
        };
    }

    /// <summary>Opt-in gate shared by every real-Hermes test in this repository.</summary>
    public static bool Enabled =>
        bool.TryParse(Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_SMOKE_TEST"), out var enabled) && enabled;

    public static LiveWorkerHost Create()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "live-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var bareRepoPath = LiveRepairFixture.CreateBareRepo(tempDir);
        var projectsBaseDir = Path.Combine(tempDir, "workspaces");
        Directory.CreateDirectory(projectsBaseDir);

        var dbPath = Path.Combine(tempDir, "factory.db");

        var host = BuildHost(tempDir, projectsBaseDir, dbPath);

        var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite($"Data Source={dbPath}").Options;
        using (var db = new FactoryDbContext(schemaOptions))
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        return new LiveWorkerHost(host, tempDir, bareRepoPath, projectsBaseDir, dbPath);
    }

    private static IHost BuildHost(string tempDir, string projectsBaseDir, string dbPath)
    {
        var hermesData = Path.Combine(tempDir, "hermes-data");
        var hermesScratch = Path.Combine(tempDir, "hermes-scratch");
        Directory.CreateDirectory(hermesData);
        Directory.CreateDirectory(hermesScratch);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = FindWorkerProjectDirectory(),
            EnvironmentName = "Production"
        });

        Program.ConfigureServices(builder);

        var dbDescriptor = builder.Services.Single(d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));
        builder.Services.Remove(dbDescriptor);
        builder.Services.AddDbContext<FactoryDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

        var workspaceOptions = (AgentWorkspaceOptions)builder.Services
            .Single(d => d.ServiceType == typeof(AgentWorkspaceOptions)).ImplementationInstance!;
        workspaceOptions.ProjectsBaseDirectory = projectsBaseDir;
        workspaceOptions.AllowedRoot = tempDir;

        var schedulerOptions = (TaskSchedulerOptions)builder.Services
            .Single(d => d.ServiceType == typeof(TaskSchedulerOptions)).ImplementationInstance!;
        schedulerOptions.MaxConcurrentTasks = 1;
        // Phase 18.25 — the live lifecycle test runs the real
        // TaskSchedulerHostedService instead of pumping cycles by hand, so the
        // shipped 15s tick would dominate the run's wall clock. Only the
        // interval is shortened; Enabled, MaxConcurrentTasks and every timeout
        // stay at their production values.
        schedulerOptions.SchedulingInterval = TimeSpan.FromSeconds(2);

        var hermesOptions = (HermesOptions)builder.Services
            .Single(d => d.ServiceType == typeof(HermesOptions)).ImplementationInstance!;
        hermesOptions.DockerImage = Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_IMAGE")
            ?? "nousresearch/hermes-agent:latest";
        hermesOptions.DataVolume = hermesData;
        hermesOptions.WorkspaceRoot = hermesScratch;
        hermesOptions.NetworkMode = "bridge";
        hermesOptions.Provider = "openrouter";
        hermesOptions.ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY";
        hermesOptions.ProviderApiKeySecretName = LiveApiKeySecretName;
        hermesOptions.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = LiveBaseUrl;
        hermesOptions.AdditionalEnvironmentVariables["HERMES_WRITE_SAFE_ROOT"] = "/workspace";
        hermesOptions.DefaultModel = LiveModel;

        // Per-role routing through the existing catalog rather than any
        // provider-specific branch, so Implementation, Repair, Review, Test and
        // Planner can be pointed at different models purely by environment.
        var routingOptions = (ModelRoutingOptions)builder.Services
            .Single(d => d.ServiceType == typeof(ModelRoutingOptions)).ImplementationInstance!;

        var roleCategories = new (string Role, string Category)[]
        {
            ("implementation", "coding"),
            ("repair", "debugging"),
            ("review", "code-review"),
            ("planner", "planning"),
            ("test", "testing")
        };

        routingOptions.Models.Clear();
        routingOptions.CategoryProfiles.Clear();

        foreach (var (role, category) in roleCategories)
        {
            // Phase 18.26 §9/§10 — index 0 is the role's primary, index 1 its
            // fallback. A category profile was always an ordered candidate
            // list, so expressing the policy needed no new configuration shape.
            var profile = new List<string> { ModelForRole(role) };

            if (FallbackModelForRole(role) is { } fallbackId
                && !string.Equals(fallbackId, profile[0], StringComparison.OrdinalIgnoreCase))
            {
                profile.Add(fallbackId);
            }

            foreach (var modelId in profile)
            {
                if (routingOptions.Models.Any(m => string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                routingOptions.Models.Add(new ModelCapability
                {
                    ModelId = modelId,
                    Provider = "openrouter",
                    ReasoningStrength = 4,
                    MaxContextTokens = 32_000,
                    CostTier = 1,
                    IsCodingOptimized = true,
                    IsAvailable = true
                });
            }

            routingOptions.CategoryProfiles[category] = profile;
        }

        Assert.Empty(routingOptions.Validate());

        builder.Services.AddScoped<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddScoped<IProjectWorkspaceProvisioner, ProjectWorkspaceProvisioner>();
        builder.Services.AddScoped<IProjectSourceInitializer,
            AI.Factory.Infrastructure.Source.ProjectSourceInitializer>();
        builder.Services.AddScoped<AI.Factory.Application.Projects.ProjectService>();
        builder.Services.AddScoped<AI.Factory.Application.Tasks.WorkItemService>();
        builder.Services.AddScoped<AI.Factory.Application.Tasks.TaskDependencyService>();

        return builder.Build();
    }

    private static string FindWorkerProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate AI.Factory.slnx above the test assembly.");
        }

        return Path.Combine(dir.FullName, "src", "AI.Factory.Worker");
    }

    public void Dispose()
    {
        Host.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup — a leftover temp dir must never fail the run
        }
    }
}
