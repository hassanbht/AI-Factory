using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.15 — proves ProjectWorkspaceProvisioner's directory-creation
/// contract in isolation: idempotent creation, file-vs-directory conflict
/// detection, and reuse of the same AllowedRoot boundary WorkspaceResolverTests
/// already established for ProjectWorkspaceResolver (spec §10 — provisioner
/// and resolver must never independently decide path validity).
/// </summary>
public sealed class ProjectWorkspaceProvisionerTests : IDisposable
{
    private readonly string _tempRoot;

    public ProjectWorkspaceProvisionerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ai-factory-workspace-provisioner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static Project MakeProject(string rootPath) => new()
    {
        Name = "Test Project",
        Slug = "test-project",
        RootPath = rootPath
    };

    private static ProjectWorkspaceProvisioner CreateProvisioner(AgentWorkspaceOptions options) =>
        new(new ProjectWorkspaceResolver(options), options, NullLogger<ProjectWorkspaceProvisioner>.Instance);

    [Fact]
    public void Provision_MissingDirectory_CreatesIt()
    {
        var provisioner = CreateProvisioner(new AgentWorkspaceOptions());
        var target = Path.Combine(_tempRoot, "new-project");
        var project = MakeProject(target);

        var result = provisioner.Provision(project);

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(target), result.Path);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Provision_RepeatedCalls_AreIdempotent()
    {
        var provisioner = CreateProvisioner(new AgentWorkspaceOptions());
        var target = Path.Combine(_tempRoot, "repeat-project");
        var project = MakeProject(target);

        var first = provisioner.Provision(project);
        var second = provisioner.Provision(project);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Path, second.Path);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Provision_AlreadyExistingDirectory_IsAcceptedAsSuccess()
    {
        var target = Path.Combine(_tempRoot, "already-there");
        Directory.CreateDirectory(target);

        var provisioner = CreateProvisioner(new AgentWorkspaceOptions());
        var result = provisioner.Provision(MakeProject(target));

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(target), result.Path);
    }

    [Fact]
    public void Provision_ExistingFileAtTargetPath_IsRejected_WithoutThrowing()
    {
        var target = Path.Combine(_tempRoot, "a-file.txt");
        File.WriteAllText(target, "not a directory");

        var provisioner = CreateProvisioner(new AgentWorkspaceOptions());
        var result = provisioner.Provision(MakeProject(target));

        Assert.False(result.Success);
        Assert.Null(result.Path);
        Assert.Contains("file", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Provision_InvalidRootPath_IsRejected(string? rootPath)
    {
        var provisioner = CreateProvisioner(new AgentWorkspaceOptions());
        var result = provisioner.Provision(MakeProject(rootPath!));

        Assert.False(result.Success);
        Assert.Null(result.Path);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void Provision_AllowedRootConfigured_PathInsideRoot_Succeeds()
    {
        var provisioner = CreateProvisioner(new AgentWorkspaceOptions { AllowedRoot = _tempRoot });
        var target = Path.Combine(_tempRoot, "inside-project");

        var result = provisioner.Provision(MakeProject(target));

        Assert.True(result.Success);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Provision_AllowedRootConfigured_SiblingPathOutsideRoot_IsRejected_NoDirectoryCreated()
    {
        var sibling = Path.Combine(Path.GetTempPath(), "ai-factory-workspace-provisioner-sibling-" + Guid.NewGuid().ToString("N"));

        try
        {
            var provisioner = CreateProvisioner(new AgentWorkspaceOptions { AllowedRoot = _tempRoot });
            var result = provisioner.Provision(MakeProject(sibling));

            Assert.False(result.Success);
            Assert.Contains("outside", result.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(sibling));
        }
        finally
        {
            if (Directory.Exists(sibling))
            {
                Directory.Delete(sibling, recursive: true);
            }
        }
    }

    [Fact]
    public void Provision_AllowedRootConfigured_ParentPathOutsideRoot_IsRejected()
    {
        var parent = Path.GetDirectoryName(_tempRoot.TrimEnd(Path.DirectorySeparatorChar))!;
        var provisioner = CreateProvisioner(new AgentWorkspaceOptions { AllowedRoot = _tempRoot });

        var result = provisioner.Provision(MakeProject(parent));

        Assert.False(result.Success);
    }

    [Fact]
    public void Provision_AllowedRootConfigured_TraversalAttempt_IsRejected()
    {
        var traversal = Path.Combine(_tempRoot, "..", "..");
        var provisioner = CreateProvisioner(new AgentWorkspaceOptions { AllowedRoot = _tempRoot });

        var result = provisioner.Provision(MakeProject(traversal));

        Assert.False(result.Success);
    }

    [Fact]
    public void Provision_AfterSuccess_ResolverAlsoResolvesTheSamePath()
    {
        // Spec §10 — "Provisioner creates, Resolver validates": prove the two
        // components agree, rather than each independently deciding validity.
        var options = new AgentWorkspaceOptions();
        var target = Path.Combine(_tempRoot, "agreement-project");
        var project = MakeProject(target);

        var provisionResult = CreateProvisioner(options).Provision(project);
        var resolution = new ProjectWorkspaceResolver(options).Resolve(project);

        Assert.True(provisionResult.Success);
        Assert.True(resolution.IsValid);
        Assert.Equal(provisionResult.Path, resolution.Path);
    }

    /// <summary>
    /// Phase 18.16 §2/§8 — with a configured ProjectsBaseDirectory, a
    /// RELATIVE RootPath must resolve to the identical absolute path for both
    /// the Provisioner (which creates it) and the Resolver (which Agent
    /// execution and Verification both use) — proving the base directory
    /// applies through the one shared WorkspacePathResolution helper, not a
    /// per-component reimplementation.
    /// </summary>
    [Fact]
    public void Provision_ProjectsBaseDirectoryConfigured_ProvisionerAndResolverAgree()
    {
        var baseDir = Path.Combine(_tempRoot, "base");
        Directory.CreateDirectory(baseDir);
        var options = new AgentWorkspaceOptions { ProjectsBaseDirectory = baseDir };
        var project = MakeProject("relative-project");

        var provisionResult = CreateProvisioner(options).Provision(project);
        var resolution = new ProjectWorkspaceResolver(options).Resolve(project);

        Assert.True(provisionResult.Success, provisionResult.FailureReason);
        Assert.True(resolution.IsValid, resolution.FailureReason);
        Assert.Equal(provisionResult.Path, resolution.Path);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "relative-project")), provisionResult.Path);
    }
}
