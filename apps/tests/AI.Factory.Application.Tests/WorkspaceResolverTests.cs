using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.14 — proves ProjectWorkspaceResolver's resolution/validation
/// contract in isolation, including reuse of the existing WorkspacePathGuard
/// for the optional AllowedRoot boundary check.
/// </summary>
public sealed class WorkspaceResolverTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ai-factory-workspace-resolver-tests", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Resolve_ValidExistingRootPath_ReturnsValidAbsolutePath()
    {
        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions());
        var project = MakeProject(_tempRoot);

        var result = resolver.Resolve(project);

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(_tempRoot), result.Path);
        Assert.Null(result.FailureReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NullOrWhitespaceRootPath_ReturnsInvalid(string? rootPath)
    {
        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions());
        var project = MakeProject(rootPath!);

        var result = resolver.Resolve(project);

        Assert.False(result.IsValid);
        Assert.Null(result.Path);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void Resolve_NonexistentDirectory_ReturnsInvalid()
    {
        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions());
        var project = MakeProject(Path.Combine(_tempRoot, "does-not-exist"));

        var result = resolver.Resolve(project);

        Assert.False(result.IsValid);
        Assert.Contains("does not exist", result.FailureReason);
    }

    [Fact]
    public void Resolve_NoAllowedRootConfigured_AnyValidExistingPathPasses_WithoutBoundaryCheck()
    {
        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions { AllowedRoot = null });
        var elsewhere = Path.Combine(Path.GetTempPath(), "ai-factory-workspace-resolver-elsewhere-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);

        try
        {
            var result = resolver.Resolve(MakeProject(elsewhere));
            Assert.True(result.IsValid);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void Resolve_AllowedRootConfigured_PathInsideRoot_Passes()
    {
        var inside = Path.Combine(_tempRoot, "project-a");
        Directory.CreateDirectory(inside);

        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions { AllowedRoot = _tempRoot });

        var result = resolver.Resolve(MakeProject(inside));

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(inside), result.Path);
    }

    [Fact]
    public void Resolve_AllowedRootConfigured_SiblingPathOutsideRoot_IsRejected()
    {
        var sibling = Path.Combine(Path.GetTempPath(), "ai-factory-workspace-resolver-sibling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sibling);

        try
        {
            var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions { AllowedRoot = _tempRoot });

            var result = resolver.Resolve(MakeProject(sibling));

            Assert.False(result.IsValid);
            Assert.Contains("outside", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void Resolve_AllowedRootConfigured_ParentPathOutsideRoot_IsRejected()
    {
        var parent = Path.GetDirectoryName(_tempRoot.TrimEnd(Path.DirectorySeparatorChar))!;
        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions { AllowedRoot = _tempRoot });

        var result = resolver.Resolve(MakeProject(parent));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Resolve_AllowedRootConfigured_TraversalAttempt_IsRejected()
    {
        var traversal = Path.Combine(_tempRoot, "..", "..");
        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions { AllowedRoot = _tempRoot });

        var result = resolver.Resolve(MakeProject(traversal));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Resolve_SameProject_ResolvesSamePath_Deterministically()
    {
        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions());
        var project = MakeProject(_tempRoot);

        var first = resolver.Resolve(project);
        var second = resolver.Resolve(project);

        Assert.Equal(first.Path, second.Path);
        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
    }

    // ---------------- Phase 18.16 §2/§8 — ProjectsBaseDirectory ----------------

    /// <summary>Backward-compatibility regression (spec §8/B7): unset ProjectsBaseDirectory must reproduce the exact prior CWD-relative behavior.</summary>
    [Fact]
    public void Resolve_ProjectsBaseDirectoryUnset_ReproducesPriorCwdRelativeBehavior()
    {
        var relativeSegment = Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.Combine(_tempRoot, "cwd-relative"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "cwd-relative"));

        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions { ProjectsBaseDirectory = null });
        var result = resolver.Resolve(MakeProject(relativeSegment));

        Assert.True(result.IsValid, result.FailureReason);
        Assert.Equal(Path.GetFullPath(relativeSegment), result.Path);
    }

    [Fact]
    public void Resolve_ProjectsBaseDirectoryConfigured_RelativeRootPath_ResolvesUnderBase()
    {
        var baseDir = Path.Combine(_tempRoot, "base");
        var target = Path.Combine(baseDir, "my-project");
        Directory.CreateDirectory(target);

        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions { ProjectsBaseDirectory = baseDir });
        var result = resolver.Resolve(MakeProject("my-project"));

        Assert.True(result.IsValid, result.FailureReason);
        Assert.Equal(Path.GetFullPath(target), result.Path);
    }

    [Fact]
    public void Resolve_ProjectsBaseDirectoryConfigured_AbsoluteRootPath_IsUnaffected()
    {
        var baseDir = Path.Combine(_tempRoot, "base");
        Directory.CreateDirectory(baseDir);

        var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions { ProjectsBaseDirectory = baseDir });
        var result = resolver.Resolve(MakeProject(_tempRoot));

        Assert.True(result.IsValid, result.FailureReason);
        Assert.Equal(Path.GetFullPath(_tempRoot), result.Path);
    }

    [Fact]
    public void Resolve_ProjectsBaseDirectoryConfigured_ChangingProcessCwd_DoesNotChangeResolution()
    {
        var baseDir = Path.Combine(_tempRoot, "base-cwd-independent");
        var target = Path.Combine(baseDir, "my-project");
        Directory.CreateDirectory(target);

        var elsewhereCwd = Path.Combine(_tempRoot, "elsewhere-cwd");
        Directory.CreateDirectory(elsewhereCwd);

        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            var resolver = new ProjectWorkspaceResolver(new AgentWorkspaceOptions { ProjectsBaseDirectory = baseDir });
            var before = resolver.Resolve(MakeProject("my-project"));

            Directory.SetCurrentDirectory(elsewhereCwd);
            var after = resolver.Resolve(MakeProject("my-project"));

            Assert.True(before.IsValid, before.FailureReason);
            Assert.True(after.IsValid, after.FailureReason);
            Assert.Equal(before.Path, after.Path);
            Assert.Equal(Path.GetFullPath(target), after.Path);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public void Resolve_ProjectsBaseDirectoryConfigured_WithAllowedRootConfigured_BothApply()
    {
        var baseDir = Path.Combine(_tempRoot, "base");
        var target = Path.Combine(baseDir, "my-project");
        Directory.CreateDirectory(target);

        var resolver = new ProjectWorkspaceResolver(
            new AgentWorkspaceOptions { ProjectsBaseDirectory = baseDir, AllowedRoot = baseDir });
        var result = resolver.Resolve(MakeProject("my-project"));

        Assert.True(result.IsValid, result.FailureReason);
        Assert.Equal(Path.GetFullPath(target), result.Path);
    }

    [Fact]
    public void Resolve_ProjectsBaseDirectoryOutsideAllowedRoot_IsRejectedDeterministically()
    {
        var baseDir = Path.Combine(_tempRoot, "base-outside-allowed");
        var allowedRoot = Path.Combine(_tempRoot, "allowed-root");
        Directory.CreateDirectory(Path.Combine(baseDir, "my-project"));
        Directory.CreateDirectory(allowedRoot);

        var resolver = new ProjectWorkspaceResolver(
            new AgentWorkspaceOptions { ProjectsBaseDirectory = baseDir, AllowedRoot = allowedRoot });
        var result = resolver.Resolve(MakeProject("my-project"));

        Assert.False(result.IsValid);
        Assert.Contains("outside", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_ProjectsBaseDirectoryConfigured_TraversalShapedRootPath_CannotEscapeBase_WhenAllowedRootIsBase()
    {
        var baseDir = Path.Combine(_tempRoot, "base-traversal");
        Directory.CreateDirectory(baseDir);

        var resolver = new ProjectWorkspaceResolver(
            new AgentWorkspaceOptions { ProjectsBaseDirectory = baseDir, AllowedRoot = baseDir });
        var result = resolver.Resolve(MakeProject(Path.Combine("..", "..", "escaped")));

        Assert.False(result.IsValid);
    }
}
