using System.Text.Json;
using AI.Factory.Application.Configuration;
using AI.Factory.Infrastructure.Configuration;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Api.Tests.Deployment;

public sealed class WindowsPlatformOverlayTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Api_AppSettingsProductionWindows_ExistsAndContainsValidWindowsPaths()
    {
        var path = Path.Combine(RepoRoot, "apps", "src", "AI.Factory.Api", "appsettings.Production.Windows.json");
        Assert.True(File.Exists(path), $"Expected {path} to exist.");

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Check ConnectionStrings
        Assert.True(root.TryGetProperty("ConnectionStrings", out var connSection));
        var factoryDb = connSection.GetProperty("FactoryDatabase").GetString();
        Assert.NotNull(factoryDb);
        Assert.Contains("%ProgramData%", factoryDb);
        Assert.DoesNotContain("/var/lib", factoryDb);

        // Check Database section
        Assert.True(root.TryGetProperty("Database", out var dbSection));
        var dbConn = dbSection.GetProperty("ConnectionString").GetString();
        Assert.NotNull(dbConn);
        Assert.Contains("%ProgramData%", dbConn);
        Assert.True(dbSection.GetProperty("AutoMigrate").GetBoolean());

        // Check Backup
        Assert.True(root.TryGetProperty("Backup", out var backupSection));
        var backupDir = backupSection.GetProperty("BackupDirectory").GetString();
        Assert.NotNull(backupDir);
        Assert.Contains("%ProgramData%", backupDir);

        // Check AgentWorkspace
        Assert.True(root.TryGetProperty("AgentWorkspace", out var workspaceSection));
        var projectsBase = workspaceSection.GetProperty("ProjectsBaseDirectory").GetString();
        var allowedRoot = workspaceSection.GetProperty("AllowedRoot").GetString();
        Assert.NotNull(projectsBase);
        Assert.NotNull(allowedRoot);
        Assert.Contains("%ProgramData%", projectsBase);
        Assert.Contains("%ProgramData%", allowedRoot);
    }

    [Fact]
    public void Worker_AppSettingsProductionWindows_ExistsAndContainsValidWindowsPaths()
    {
        var path = Path.Combine(RepoRoot, "apps", "src", "AI.Factory.Worker", "appsettings.Production.Windows.json");
        Assert.True(File.Exists(path), $"Expected {path} to exist.");

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("ConnectionStrings", out var connSection));
        var factoryDb = connSection.GetProperty("FactoryDatabase").GetString();
        Assert.NotNull(factoryDb);
        Assert.Contains("%ProgramData%", factoryDb);
        Assert.DoesNotContain("/var/lib", factoryDb);

        Assert.True(root.TryGetProperty("Hermes", out var hermesSection));
        var hermesWorkspace = hermesSection.GetProperty("WorkspaceRoot").GetString();
        Assert.NotNull(hermesWorkspace);
        Assert.Contains("%ProgramData%", hermesWorkspace);
        // Phase M — Hermes' home is a per-run anonymous volume, never a configured host path.
        Assert.False(hermesSection.TryGetProperty("DataVolume", out _));

        Assert.True(root.TryGetProperty("AgentWorkspace", out var workspaceSection));
        var projectsBase = workspaceSection.GetProperty("ProjectsBaseDirectory").GetString();
        Assert.NotNull(projectsBase);
        Assert.Contains("%ProgramData%", projectsBase);
    }

    [Fact]
    public void DatabaseOptions_AutoMigrate_DefaultsToFalse_AndBindsFromConfiguration()
    {
        var options = new DatabaseOptions();
        Assert.False(options.AutoMigrate);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Data Source=test.db",
                ["Database:AutoMigrate"] = "true"
            })
            .Build();

        config.GetSection("Database").Bind(options);
        Assert.True(options.AutoMigrate);
    }

    [Fact]
    public void EnsureDataDirectoryExists_CreatesDirectoryAndProbesWritability()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), $"ai_factory_test_data_{Guid.NewGuid():N}");
        var dbFile = Path.Combine(tempFolder, "subfolder", "factory.db");
        var connectionString = $"Data Source={dbFile}";

        try
        {
            Assert.False(Directory.Exists(Path.GetDirectoryName(dbFile)));

            SqliteStorageInitializer.EnsureDataDirectoryExists(connectionString, NullLogger.Instance);

            Assert.True(Directory.Exists(Path.GetDirectoryName(dbFile)));
        }
        finally
        {
            if (Directory.Exists(tempFolder))
            {
                Directory.Delete(tempFolder, true);
            }
        }
    }

    [Fact]
    public void AddPlatformSpecificOverlay_WhenOnWindows_OverridesLinuxPathsWithWindowsOverlay()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Platform overlay test specific to Windows
        }

        var apiProjectDir = Path.Combine(RepoRoot, "apps", "src", "AI.Factory.Api");
        var env = new TestHostEnvironment
        {
            ContentRootPath = apiProjectDir,
            ContentRootFileProvider = new PhysicalFileProvider(apiProjectDir),
            EnvironmentName = "Production"
        };

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(apiProjectDir)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Production.json", optional: false);

        configBuilder.AddPlatformSpecificOverlay(env);
        var config = configBuilder.Build();

        var dbOptions = new DatabaseOptions();
        config.GetSection("Database").Bind(dbOptions);

        Assert.Contains("%ProgramData%", dbOptions.ConnectionString);
        Assert.DoesNotContain("/var/lib", dbOptions.ConnectionString);
        Assert.True(dbOptions.AutoMigrate);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "AI.Factory.slnx");
            if (File.Exists(candidate))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "AI.Factory.Api";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
