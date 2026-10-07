using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Xunit;

namespace AI.Factory.Application.Tests.Orchestration;

/// <summary>
/// Phase P.1 — Workspace Integrity Positive Allowlist Tests (Fix G-P3).
/// Validates Group D:
/// - unexpected ordinary file;
/// - unexpected Unicode filename (lossless reporting with code points);
/// - unexpected hidden file (.env, .git/config, .bash_history);
/// - unexpected directory;
/// - unexpected nested repository / gitlinks;
/// - credential-like artifact;
/// - permitted intended artifact;
/// - clean workspace.
/// </summary>
public sealed class WorkspaceIntegrityPositiveAllowlistTests
{
    [Fact]
    public void CleanWorkspace_ReturnsZeroViolations()
    {
        var changes = Array.Empty<WorkspaceFileChange>();
        var violations = WorkspaceIntegrityPolicy.Evaluate(changes);
        Assert.Empty(violations);
    }

    [Fact]
    public void PermittedIntendedArtifacts_PassIntegrityCheck()
    {
        var permittedChanges = new[]
        {
            new WorkspaceFileChange("src/Core/PrimeService.cs", WorkspaceChangeKind.Added),
            new WorkspaceFileChange("tests/Core.Tests/PrimeServiceTests.cs", WorkspaceChangeKind.Added),
            new WorkspaceFileChange("docs/design.md", WorkspaceChangeKind.Added),
            new WorkspaceFileChange("src/Core/PrimeService.cs", WorkspaceChangeKind.Modified),
            new WorkspaceFileChange("src/OldService.cs", WorkspaceChangeKind.Deleted)
        };

        var violations = WorkspaceIntegrityPolicy.Evaluate(permittedChanges);
        Assert.Empty(violations);
    }

    [Fact]
    public void UnexpectedOrdinaryFile_IsFlaggedAsViolation()
    {
        var changes = new[]
        {
            new WorkspaceFileChange("rogue_script.py", WorkspaceChangeKind.Added)
        };

        var violations = WorkspaceIntegrityPolicy.Evaluate(changes);
        var violation = Assert.Single(violations);
        Assert.Contains("rogue_script.py", violation);
    }

    [Fact]
    public void UnexpectedUnicodeFilename_IsFlaggedWithLosslessCodePointFormatting()
    {
        // Unicode file like U+F00D observed in G-P3:
        var unicodePath = "\uF00D";
        var changes = new[]
        {
            new WorkspaceFileChange(unicodePath, WorkspaceChangeKind.Added)
        };

        var violations = WorkspaceIntegrityPolicy.Evaluate(changes);
        var violation = Assert.Single(violations);
        Assert.Contains("U+F00D", violation);
    }

    [Fact]
    public void UnexpectedHiddenFile_IsFlaggedAsViolation()
    {
        var changes = new[]
        {
            new WorkspaceFileChange(".hidden_payload", WorkspaceChangeKind.Added),
            new WorkspaceFileChange("src/.config_cache", WorkspaceChangeKind.Added)
        };

        var violations = WorkspaceIntegrityPolicy.Evaluate(changes);
        Assert.Equal(2, violations.Count);
        Assert.All(violations, v => Assert.Contains("unexpected untracked file", v));
    }

    [Fact]
    public void CredentialLikeArtifact_IsFlaggedAsViolation()
    {
        var changes = new[]
        {
            new WorkspaceFileChange(".env", WorkspaceChangeKind.Added),
            new WorkspaceFileChange("id_rsa", WorkspaceChangeKind.Added),
            new WorkspaceFileChange("api_key.txt", WorkspaceChangeKind.Added)
        };

        var violations = WorkspaceIntegrityPolicy.Evaluate(changes);
        Assert.Equal(3, violations.Count);
    }

    [Fact]
    public void UnexpectedDirectory_IsFlaggedAsViolation()
    {
        var changes = new[]
        {
            new WorkspaceFileChange("random_folder/data.txt", WorkspaceChangeKind.Added)
        };

        var violations = WorkspaceIntegrityPolicy.Evaluate(changes);
        var violation = Assert.Single(violations);
        Assert.Contains("random_folder/data.txt", violation);
    }

    [Fact]
    public void UnexpectedNestedRepositoryOrGitLink_IsFlaggedAsViolation()
    {
        var changes = new[]
        {
            new WorkspaceFileChange(".git/config", WorkspaceChangeKind.Added),
            new WorkspaceFileChange("submodule/.git", WorkspaceChangeKind.Added)
        };

        var violations = WorkspaceIntegrityPolicy.Evaluate(changes);
        Assert.Equal(2, violations.Count);
    }
}
