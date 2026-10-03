using System.Diagnostics;
using AI.Factory.Application.Orchestration;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase M — workspace integrity (benchmark T1/T2 findings): the rules on their own, then the git inspector
/// against a real temporary repository that replays what factory-T1-r2 / factory-T2-r1 left behind.
/// </summary>
public sealed class WorkspaceIntegrityTests : IDisposable
{
    private const string BaselineCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="xunit" Version="2.9.2" />
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
          </ItemGroup>
        </Project>
        """;

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "aif-integrity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // Best effort: git may still be writing short-lived files under .git when a test ends.
        try
        {
            if (Directory.Exists(_repo))
            {
                foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_repo, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static WorkspaceFileChange Added(string path, string? content = "x") => new(path, WorkspaceChangeKind.Added, null, content);

    [Theory]
    [InlineData("src/Inventory/Reservation.cs")]
    [InlineData("tests/Inventory.Tests/ReservationTests.cs")]
    [InlineData("docs/notes/design.md")]
    public void IntendedSourceAndTestChanges_AreAllowed(string path)
    {
        Assert.Empty(WorkspaceIntegrityPolicy.Evaluate(new[] { Added(path), new WorkspaceFileChange(path, WorkspaceChangeKind.Modified) }));
    }

    [Theory]
    [InlineData("dotnet-install.sh")]                       // factory-T1-r2
    [InlineData("BUILD_SUMMARY.txt")]                       // factory-T2-r1
    [InlineData("artifacts/runs/abc/implementation.md")]    // archon-T2-r2 / T3-r2
    [InlineData(".claude/settings.json")]
    [InlineData(".codex/sessions/rollout.jsonl")]
    [InlineData(".hermes/.env")]
    [InlineData(".env")]
    [InlineData("auth.json")]
    [InlineData("agent.log")]
    [InlineData("state.sqlite")]
    [InlineData(".aif-hermes-run-0123.json")]
    public void RuntimeStateCredentialsLogsInstallersAndReports_AreViolations(string path)
    {
        var violation = Assert.Single(WorkspaceIntegrityPolicy.Evaluate(new[] { Added(path) }));
        Assert.Contains(path, violation);
    }

    [Theory]
    [InlineData("Inventory.slnx")]
    [InlineData("global.json")]
    [InlineData("Directory.Build.props")]
    [InlineData(".gitignore")]
    public void RewritingBuildInfrastructure_IsAViolation(string path)
    {
        Assert.Single(WorkspaceIntegrityPolicy.Evaluate(new[] { new WorkspaceFileChange(path, WorkspaceChangeKind.Modified) }));
    }

    /// <summary>factory-T2-r1: the test csproj lost its implicit usings and xunit went 2.9.2 -> 2.6.4.</summary>
    [Fact]
    public void DependencyDowngradeAndProjectSettingRewrite_AreViolations()
    {
        var rewritten = BaselineCsproj
            .Replace("<ImplicitUsings>enable</ImplicitUsings>", string.Empty)
            .Replace("Include=\"xunit\" Version=\"2.9.2\"", "Include=\"xunit\" Version=\"2.6.4\"");

        var violations = WorkspaceIntegrityPolicy.Evaluate(new[]
        {
            new WorkspaceFileChange("tests/Inventory.Tests/Inventory.Tests.csproj", WorkspaceChangeKind.Modified, BaselineCsproj, rewritten)
        });

        Assert.Contains(violations, v => v.Contains("'xunit' version changed 2.9.2 -> 2.6.4"));
        Assert.Contains(violations, v => v.Contains("<ImplicitUsings>"));
    }

    [Fact]
    public void AddingANewPackage_IsAllowed_ButRemovingAnExistingOneIsNot()
    {
        var added = BaselineCsproj.Replace("</ItemGroup>", "  <PackageReference Include=\"CsvHelper\" Version=\"33.0.1\" />\n  </ItemGroup>");
        var removed = BaselineCsproj.Replace("<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.11.1\" />", string.Empty);

        Assert.Empty(WorkspaceIntegrityPolicy.Evaluate(new[] { new WorkspaceFileChange("a.csproj", WorkspaceChangeKind.Modified, BaselineCsproj, added) }));
        Assert.Contains(
            WorkspaceIntegrityPolicy.Evaluate(new[] { new WorkspaceFileChange("a.csproj", WorkspaceChangeKind.Modified, BaselineCsproj, removed) }),
            v => v.Contains("'Microsoft.NET.Test.Sdk' was removed"));
    }

    [Fact]
    public void NpmDependencyVersionChange_IsAViolation()
    {
        var violations = WorkspaceIntegrityPolicy.Evaluate(new[]
        {
            new WorkspaceFileChange("package.json", WorkspaceChangeKind.Modified,
                "{\"dependencies\":{\"left-pad\":\"1.3.0\"}}", "{\"dependencies\":{\"left-pad\":\"1.0.0\"}}")
        });

        Assert.Single(violations);
    }

    [Fact]
    public void CredentialWrittenIntoAnyFile_IsAViolation()
    {
        var violations = WorkspaceIntegrityPolicy.Evaluate(new[]
        {
            Added("src/Config.cs", "const string Key = \"sk-or-v1-0123456789abcdef0123456789abcdef\";")
        });

        Assert.Contains(violations, v => v.Contains("credential"));
    }

    [Fact]
    public void IgnoredButPresentCredentialFiles_AreViolations_IgnoredBuildNoiseIsNot()
    {
        Assert.Single(WorkspaceIntegrityPolicy.Evaluate(new[] { new WorkspaceFileChange(".env", WorkspaceChangeKind.Ignored) }));
        Assert.Empty(WorkspaceIntegrityPolicy.Evaluate(new[] { new WorkspaceFileChange("coverage/report.html", WorkspaceChangeKind.Ignored) }));
    }

    // ---------------- Real git repository ----------------

    private void Git(params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static GitWorkspaceIntegrityInspector Inspector() => new(
        new SystemProcessRunner(), NullLogger<GitWorkspaceIntegrityInspector>.Instance);

    private void InitFixtureRepository()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "main");
        Git("config", "core.autocrlf", "false");
        Write(".gitignore", "bin/\nobj/\n.env\n");
        Write("Inventory.slnx", "<Solution />");
        Write("src/Inventory/StockLedger.cs", "class StockLedger {}");
        Write("tests/Inventory.Tests/Inventory.Tests.csproj", BaselineCsproj);
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@localhost", "commit", "-q", "-m", "baseline");
    }

    [Fact]
    public async Task GitInspector_ReplaysTheBenchmarkScopeCreep_AndReportsEveryViolation()
    {
        InitFixtureRepository();
        Write("src/Inventory/StockLedger.cs", "class StockLedger { void Reserve() {} }"); // intended
        Write("tests/Inventory.Tests/ReservationTests.cs", "class ReservationTests {}");   // intended
        Write("dotnet-install.sh", "#!/bin/sh");                                         // T1-r2
        Write("BUILD_SUMMARY.txt", "done");                                              // T2-r1
        Write("Inventory.slnx", "<Solution><Project Path=\"x\" /></Solution>");           // T2-r1
        Write("tests/Inventory.Tests/Inventory.Tests.csproj",                            // T2-r1
            BaselineCsproj.Replace("Version=\"2.9.2\"", "Version=\"2.6.4\""));
        Write(".env", "OPENROUTER_API_KEY=whatever");                                    // ignored, still present
        Write("src/Inventory/bin/Debug/x.dll", "binary-ish");                            // build output: ignored

        var changes = await Inspector().GetChangesAsync(_repo);
        var violations = WorkspaceIntegrityPolicy.Evaluate(changes!);

        Assert.Contains(violations, v => v.Contains("dotnet-install.sh"));
        Assert.Contains(violations, v => v.Contains("BUILD_SUMMARY.txt"));
        Assert.Contains(violations, v => v.Contains("Inventory.slnx"));
        Assert.Contains(violations, v => v.Contains("'xunit' version changed 2.9.2 -> 2.6.4"));
        Assert.Contains(violations, v => v.Contains(".env"));
        Assert.DoesNotContain(violations, v => v.Contains("StockLedger.cs") || v.Contains("ReservationTests.cs") || v.Contains("bin/"));
        Assert.Equal(5, violations.Count);
    }

    [Fact]
    public async Task GitInspector_CleanRun_HasNoViolations()
    {
        InitFixtureRepository();
        Write("src/Inventory/StockLedger.cs", "class StockLedger { void Reserve() {} }");

        var violations = WorkspaceIntegrityPolicy.Evaluate((await Inspector().GetChangesAsync(_repo))!);

        Assert.Empty(violations);
    }

    [Fact]
    public async Task GitInspector_WithoutABaseline_ReportsUnavailable_NotClean()
    {
        Directory.CreateDirectory(_repo);

        Assert.Null(await Inspector().GetChangesAsync(_repo));
    }
}
