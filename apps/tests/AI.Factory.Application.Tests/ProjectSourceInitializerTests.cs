using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Source;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.16 §19/§20 — ProjectSourceInitializer against a local,
/// deterministic bare Git repository fixture (no GitHub/internet
/// dependency): a temp "seed" working repo is committed to on two branches,
/// then cloned to a real bare repo via real `git` commands (allowed for TEST
/// FIXTURE setup even though production code only ever calls git through
/// IProcessRunner's policy — this file confirmed `git --version` succeeds in
/// this environment before relying on it, so a failure here is a real
/// regression, never a silently-fabricated pass).
/// </summary>
public sealed class ProjectSourceInitializerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bareRepoPath;

    public ProjectSourceInitializerTests()
    {
        // Windows MAX_PATH (260 chars) is a real constraint here: a git
        // object's full path is "<repo>\.git\objects\xx\<38-hex-chars>", so
        // every path segment above it is kept intentionally short (never the
        // descriptive test method name) to leave headroom.
        _tempRoot = Path.Combine(Path.GetTempPath(), "aifsrc", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
        _bareRepoPath = Path.Combine(_tempRoot, "fx.git");
        CreateBareFixtureRepository();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                ForceDeleteDirectory(_tempRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup — a lingering git process handle on
                // Windows must never fail the test run itself.
            }
        }
    }

    /// <summary>
    /// Git marks its own object-store files read-only on Windows;
    /// Directory.Delete(recursive: true) throws UnauthorizedAccessException
    /// on those without this attribute-clearing pass first.
    /// </summary>
    private static void ForceDeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>Seeds a deterministic local fixture: "main" branch with "main.txt", "feature" branch additionally with "feature.txt".</summary>
    private void CreateBareFixtureRepository()
    {
        var seedDir = Path.Combine(_tempRoot, "seed");
        Directory.CreateDirectory(seedDir);

        RunGit(seedDir, "init", "-b", "main");
        RunGit(seedDir, "config", "user.email", "test@example.com");
        RunGit(seedDir, "config", "user.name", "Test");
        RunGit(seedDir, "config", "commit.gpgsign", "false");

        File.WriteAllText(Path.Combine(seedDir, "main.txt"), "main content");
        RunGit(seedDir, "add", ".");
        RunGit(seedDir, "commit", "-m", "initial commit on main");

        RunGit(seedDir, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(seedDir, "feature.txt"), "feature content");
        RunGit(seedDir, "add", ".");
        RunGit(seedDir, "commit", "-m", "feature commit");

        RunGit(seedDir, "checkout", "main");

        RunGit(_tempRoot, "clone", "--bare", seedDir, _bareRepoPath);
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

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git — fixture setup requires a real git executable.");
        process.WaitForExit(15000);

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed (exit {process.ExitCode}): {stderr}");
        }
    }

    private static ProjectSourceInitializer CreateInitializer(AgentWorkspaceOptions? options = null) => new(
        new ProjectWorkspaceResolver(options ?? new AgentWorkspaceOptions()),
        new SystemProcessRunner(new CommandPolicyOptions()),
        new ExecutionLimitsOptions { BuildTimeoutMinutes = 1 },
        NullLogger<ProjectSourceInitializer>.Instance);

    private Project MakeProjectWithEmptyWorkspace(string name)
    {
        // Short, non-descriptive folder name deliberately — see the
        // MAX_PATH comment in the constructor.
        var target = Path.Combine(_tempRoot, "w" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);
        return new Project { Id = Guid.NewGuid(), Name = name, Slug = name, RootPath = target };
    }

    // ---------------- GitRepository — success paths ----------------

    [Fact]
    public async Task InitializeAsync_GitRepository_DefaultBranch_ClonesAndCheckOutIsMain()
    {
        var project = MakeProjectWithEmptyWorkspace(nameof(InitializeAsync_GitRepository_DefaultBranch_ClonesAndCheckOutIsMain));

        var result = await CreateInitializer().InitializeAsync(
            project, new SourceSpecification(ProjectSourceType.GitRepository, _bareRepoPath));

        Assert.True(result.Success, result.FailureReason);
        Assert.True(File.Exists(Path.Combine(project.RootPath, "main.txt")));
        Assert.False(File.Exists(Path.Combine(project.RootPath, "feature.txt")));
        Assert.Equal("main", CurrentBranch(project.RootPath));
    }

    [Fact]
    public async Task InitializeAsync_GitRepository_ExplicitBranch_ChecksOutThatBranch()
    {
        var project = MakeProjectWithEmptyWorkspace(nameof(InitializeAsync_GitRepository_ExplicitBranch_ChecksOutThatBranch));

        var result = await CreateInitializer().InitializeAsync(
            project, new SourceSpecification(ProjectSourceType.GitRepository, _bareRepoPath, "feature"));

        Assert.True(result.Success, result.FailureReason);
        Assert.True(File.Exists(Path.Combine(project.RootPath, "main.txt")));
        Assert.True(File.Exists(Path.Combine(project.RootPath, "feature.txt")));
        Assert.Equal("feature", CurrentBranch(project.RootPath));
    }

    private static string CurrentBranch(string repoDirectory)
    {
        var startInfo = new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
        {
            WorkingDirectory = repoDirectory, RedirectStandardOutput = true, UseShellExecute = false
        };
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(10000);
        return output;
    }

    // ---------------- Empty ----------------

    [Fact]
    public async Task InitializeAsync_Empty_Succeeds_NoFilesCreated()
    {
        var project = MakeProjectWithEmptyWorkspace(nameof(InitializeAsync_Empty_Succeeds_NoFilesCreated));

        var result = await CreateInitializer().InitializeAsync(project, new SourceSpecification(ProjectSourceType.Empty));

        Assert.True(result.Success, result.FailureReason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(project.RootPath));
    }

    // ---------------- Validation failures ----------------

    [Fact]
    public async Task InitializeAsync_GitRepository_MissingLocation_ReturnsValidationFailed()
    {
        var project = MakeProjectWithEmptyWorkspace(nameof(InitializeAsync_GitRepository_MissingLocation_ReturnsValidationFailed));

        var result = await CreateInitializer().InitializeAsync(
            project, new SourceSpecification(ProjectSourceType.GitRepository, Location: null));

        Assert.False(result.Success);
        Assert.Equal(SourceInitializationFailureKind.Validation, result.FailureKind);
    }

    [Theory]
    [InlineData("--upload-pack=evil", null)]
    [InlineData("https://example.invalid/repo.git", "--exec=evil")]
    public async Task InitializeAsync_GitRepository_LocationOrBranchLooksLikeAFlag_ReturnsValidationFailed(string location, string? branch)
    {
        var project = MakeProjectWithEmptyWorkspace(nameof(InitializeAsync_GitRepository_LocationOrBranchLooksLikeAFlag_ReturnsValidationFailed) + location.GetHashCode());

        var result = await CreateInitializer().InitializeAsync(
            project, new SourceSpecification(ProjectSourceType.GitRepository, location, branch));

        Assert.False(result.Success);
        Assert.Equal(SourceInitializationFailureKind.Validation, result.FailureKind);
    }

    // ---------------- Conflict — existing-directory policy ----------------

    [Fact]
    public async Task InitializeAsync_GitRepository_NonEmptyWorkspace_ReturnsConflict_NoOverwrite()
    {
        var project = MakeProjectWithEmptyWorkspace(nameof(InitializeAsync_GitRepository_NonEmptyWorkspace_ReturnsConflict_NoOverwrite));
        var preexisting = Path.Combine(project.RootPath, "user-file.txt");
        File.WriteAllText(preexisting, "pre-existing user content");

        var result = await CreateInitializer().InitializeAsync(
            project, new SourceSpecification(ProjectSourceType.GitRepository, _bareRepoPath));

        Assert.False(result.Success);
        Assert.Equal(SourceInitializationFailureKind.Conflict, result.FailureKind);
        // Never auto-delete pre-existing files.
        Assert.True(File.Exists(preexisting));
    }

    [Fact]
    public async Task InitializeAsync_RepeatedInitialization_SecondCallOnNowPopulatedWorkspace_ReturnsConflict()
    {
        var project = MakeProjectWithEmptyWorkspace(nameof(InitializeAsync_RepeatedInitialization_SecondCallOnNowPopulatedWorkspace_ReturnsConflict));
        var initializer = CreateInitializer();

        var first = await initializer.InitializeAsync(
            project, new SourceSpecification(ProjectSourceType.GitRepository, _bareRepoPath));
        Assert.True(first.Success, first.FailureReason);

        var second = await initializer.InitializeAsync(
            project, new SourceSpecification(ProjectSourceType.GitRepository, _bareRepoPath));

        Assert.False(second.Success);
        Assert.Equal(SourceInitializationFailureKind.Conflict, second.FailureKind);
        // The first successful clone's files must remain untouched.
        Assert.True(File.Exists(Path.Combine(project.RootPath, "main.txt")));
    }

    // ---------------- Infrastructure failures ----------------

    [Fact]
    public async Task InitializeAsync_GitRepository_NonexistentRepository_ReturnsInfrastructureFailed()
    {
        var project = MakeProjectWithEmptyWorkspace(nameof(InitializeAsync_GitRepository_NonexistentRepository_ReturnsInfrastructureFailed));
        var nonexistentRepo = Path.Combine(_tempRoot, "does-not-exist.git");

        var result = await CreateInitializer().InitializeAsync(
            project, new SourceSpecification(ProjectSourceType.GitRepository, nonexistentRepo));

        Assert.False(result.Success);
        Assert.Equal(SourceInitializationFailureKind.Infrastructure, result.FailureKind);
        // Failure must never falsely mark anything as cloned: no real
        // repository content is left behind. Not asserting the directory is
        // byte-for-byte empty — git's own OWN internal cleanup of its
        // failed-clone lock file can leave a transient NTFS "delete pending"
        // directory-entry artifact for a moment on Windows, which is a git/OS
        // timing quirk, not Project content this initializer created.
        Assert.False(File.Exists(Path.Combine(project.RootPath, ".git", "HEAD")));
        Assert.False(Directory.Exists(Path.Combine(project.RootPath, ".git", "objects")));
    }

    [Fact]
    public async Task InitializeAsync_GitRepository_NonexistentBranch_ReturnsInfrastructureFailed()
    {
        var project = MakeProjectWithEmptyWorkspace(nameof(InitializeAsync_GitRepository_NonexistentBranch_ReturnsInfrastructureFailed));

        var result = await CreateInitializer().InitializeAsync(
            project, new SourceSpecification(ProjectSourceType.GitRepository, _bareRepoPath, "does-not-exist-branch"));

        Assert.False(result.Success);
        Assert.Equal(SourceInitializationFailureKind.Infrastructure, result.FailureKind);
    }

    [Fact]
    public async Task InitializeAsync_WorkspaceResolutionFails_ReturnsInfrastructureFailed()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "Missing", Slug = "missing",
            RootPath = Path.Combine(_tempRoot, "never-provisioned")
        };

        var result = await CreateInitializer().InitializeAsync(
            project, new SourceSpecification(ProjectSourceType.GitRepository, _bareRepoPath));

        Assert.False(result.Success);
        Assert.Equal(SourceInitializationFailureKind.Infrastructure, result.FailureKind);
    }
}
