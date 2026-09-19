using AI.Factory.Infrastructure.Validation;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.29.1 — what the verification gate actually builds.
///
/// The gate hands `dotnet build` the Project's workspace ROOT. MSBuild does not
/// recurse, so a project one directory down failed every verification with MSB1003
/// and burned the repair loop's bounded attempts on a framing problem rather than a
/// code problem. These tests pin both halves of the fix: the existing root-level
/// behavior is untouched, and the nested fallback only fires when unambiguous.
/// </summary>
public sealed class MsBuildTargetResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ai-factory-target-tests", Guid.NewGuid().ToString("N"));

    public MsBuildTargetResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string Touch(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void ProjectAtTheRoot_LeavesThePathUntouched()
    {
        Touch("app.csproj");

        // Unchanged from the pre-fix behavior: MSBuild resolves the directory itself.
        Assert.Equal(_root, MsBuildTargetResolver.Resolve(_root));
    }

    [Fact]
    public void SolutionAtTheRoot_LeavesThePathUntouched()
    {
        Touch("app.slnx");

        Assert.Equal(_root, MsBuildTargetResolver.Resolve(_root));
    }

    [Fact]
    public void NestedProject_IsResolved()
    {
        var expected = Touch("golden-v1", "golden-v1.csproj");

        Assert.Equal(expected, MsBuildTargetResolver.Resolve(_root));
    }

    [Fact]
    public void NestedSolution_WinsOverNestedProjects()
    {
        var solution = Touch("src", "everything.sln");
        Touch("src", "a", "a.csproj");
        Touch("src", "b", "b.csproj");

        Assert.Equal(solution, MsBuildTargetResolver.Resolve(_root));
    }

    [Fact]
    public void ShallowestProject_Wins()
    {
        var shallow = Touch("app", "app.csproj");
        Touch("app", "nested", "deeper", "deeper.csproj");

        Assert.Equal(shallow, MsBuildTargetResolver.Resolve(_root));
    }

    [Fact]
    public void AmbiguousSiblings_AreLeftToMsBuildRatherThanGuessed()
    {
        Touch("one", "one.csproj");
        Touch("two", "two.csproj");

        // Silently verifying "one" would mean reporting a green gate for a project
        // nobody chose. Failing loudly is the correct outcome.
        Assert.Equal(_root, MsBuildTargetResolver.Resolve(_root));
    }

    [Fact]
    public void BuildOutputDirectories_AreNeverBuildTargets()
    {
        Touch("obj", "stale.csproj");
        Touch("bin", "Debug", "copied.csproj");
        var real = Touch("app", "app.csproj");

        Assert.Equal(real, MsBuildTargetResolver.Resolve(_root));
    }

    [Fact]
    public void AnExplicitFilePath_IsPassedThroughUnchanged()
    {
        var file = Touch("app", "app.csproj");

        Assert.Equal(file, MsBuildTargetResolver.Resolve(file));
    }

    [Fact]
    public void ADirectoryWithNoTargetAtAll_IsReturnedUnchanged()
    {
        Touch("README.md");

        // Preserves MSBuild's own MSB1003 error rather than inventing a different one.
        Assert.Equal(_root, MsBuildTargetResolver.Resolve(_root));
    }

    [Fact]
    public void AMissingDirectory_IsReturnedUnchanged()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        Assert.Equal(missing, MsBuildTargetResolver.Resolve(missing));
    }
}
