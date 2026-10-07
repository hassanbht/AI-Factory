using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase P.1 (suite A) — the review bundle is the reviewer's actual input:
/// modified content, newly created file content, deleted content, the real diff,
/// and full context even when no workspace/diff exists (an empty workspace never
/// causes a context-less review).
/// </summary>
public sealed class ReviewBundleBuilderTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "aif-review-bundle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
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

    private sealed class FixedWorkspaceResolver : IWorkspaceResolver
    {
        private readonly string? _path;

        public FixedWorkspaceResolver(string? path) => _path = path;

        public WorkspaceResolution Resolve(Domain.Entities.Project project) =>
            _path is null ? WorkspaceResolution.Invalid("no workspace") : WorkspaceResolution.Valid(_path);
    }

    private void Git(params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
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

    private void InitRepository()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "main");
        Git("config", "core.autocrlf", "false");
        Write("src/Calculator.cs", "namespace Demo;\n\npublic static class Calculator\n{\n    public static int Add(int a, int b) => a + b;\n}\n");
        Write("src/Legacy.cs", "public static class Legacy\n{\n}\n");
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@localhost", "commit", "-q", "-m", "baseline");
    }

    private ReviewBundleBuilder Builder(string? workspacePath) => new(
        new FixedWorkspaceResolver(workspacePath),
        new GitWorkspaceIntegrityInspector(new SystemProcessRunner(), NullLogger<GitWorkspaceIntegrityInspector>.Instance));

    private static ReviewContext Context() => new(
        WorkItemId: Guid.NewGuid(),
        ProjectId: Guid.NewGuid(),
        RequirementsText: "Add a prime function with tests.",
        AcceptanceCriteria: "TestsPass",
        ArchitectureText: "Clean architecture.",
        ChangedFiles: Array.Empty<string>(),
        TestOutput: "Passed: 3, Failed: 0",
        TestsSucceeded: true,
        BuildOutput: "Build succeeded.",
        BuildSucceeded: true,
        TaskHistoryText: "history");

    private static readonly Domain.Entities.Project Project = new()
    {
        Id = Guid.NewGuid(),
        Name = "bundle",
        Slug = "bundle",
        RootPath = "unused",
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public async Task ModifiedFile_TheReviewerReceivesBothBaselineAndFinalContent_AndTheDiff()
    {
        InitRepository();
        Write("src/Calculator.cs", "namespace Demo;\n\npublic static class Calculator\n{\n    public static int Add(int a, int b) => a + b;\n    public static int Multiply(int a, int b) => a * b;\n}\n");

        var bundle = await Builder(_repo).BuildAsync(Project, Context());

        Assert.Equal(ReviewBundle.SourceGitBaseline, bundle.Source);
        var change = Assert.Single(bundle.Files, f => f.Path == "src/Calculator.cs");
        Assert.Equal(ReviewChangeKind.Modified, change.Kind);
        Assert.Contains("public static int Add", change.BaselineContent);
        Assert.Contains("Multiply", change.FinalContent);
        Assert.DoesNotContain("Multiply", change.BaselineContent);

        Assert.Contains("--- a/src/Calculator.cs (baseline)", bundle.ChangesMarkdown);
        Assert.Contains("+++ b/src/Calculator.cs (final)", bundle.ChangesMarkdown);
        Assert.Contains("+    public static int Multiply", bundle.ChangesMarkdown);
    }

    [Fact]
    public async Task NewlyCreatedFile_TheReviewerReceivesTheActualContent()
    {
        InitRepository();
        Write("src/Prime.cs", "public static class Prime\n{\n    public static bool IsPrime(int value) => value > 1;\n}\n");

        var bundle = await Builder(_repo).BuildAsync(Project, Context());

        var change = Assert.Single(bundle.Files, f => f.Path == "src/Prime.cs");
        Assert.Equal(ReviewChangeKind.Added, change.Kind);
        Assert.Null(change.BaselineContent);
        Assert.Contains("IsPrime", change.FinalContent);
        Assert.Contains("+public static class Prime", bundle.ChangesMarkdown);
    }

    [Fact]
    public async Task DeletedFile_TheReviewerReceivesTheRemovedContent()
    {
        InitRepository();
        File.Delete(Path.Combine(_repo, "src", "Legacy.cs"));

        var bundle = await Builder(_repo).BuildAsync(Project, Context());

        var change = Assert.Single(bundle.Files, f => f.Path == "src/Legacy.cs");
        Assert.Equal(ReviewChangeKind.Deleted, change.Kind);
        Assert.Contains("public static class Legacy", change.BaselineContent);
        Assert.Null(change.FinalContent);
        Assert.Contains("-public static class Legacy", bundle.ChangesMarkdown);
    }

    [Fact]
    public async Task ContextDocument_CarriesRequirementsCriteriaAndVerificationResults()
    {
        InitRepository();

        var bundle = await Builder(_repo).BuildAsync(Project, Context());

        Assert.Contains("prime function", bundle.ContextMarkdown);
        Assert.Contains("TestsPass", bundle.ContextMarkdown);
        Assert.Contains("Build succeeded", bundle.ContextMarkdown);
        Assert.Contains("Passed: 3, Failed: 0", bundle.ContextMarkdown);
        Assert.Contains("Build succeeded: True", bundle.ContextMarkdown);
    }

    [Fact]
    public async Task NoWorkspaceAtAll_StillProducesTheFullContextBundle_WithAnExplicitNoDiffNote()
    {
        var bundle = await Builder(null).BuildAsync(Project, Context());

        Assert.Equal(ReviewBundle.SourceContextOnly, bundle.Source);
        Assert.Contains("could not be resolved", bundle.ChangesMarkdown);
        Assert.Contains("prime function", bundle.ContextMarkdown);
        Assert.Contains("Passed: 3, Failed: 0", bundle.ContextMarkdown);
    }

    [Fact]
    public async Task SeedFiles_AreTheTwoBundleDocuments_ReadyForTheReviewerWorkspace()
    {
        InitRepository();
        Write("src/Prime.cs", "public static class Prime {}\n");

        var bundle = await Builder(_repo).BuildAsync(Project, Context());
        var seeds = bundle.ToSeedFiles();

        Assert.Equal(new[] { ReviewBundle.ContextFileName, ReviewBundle.ChangesFileName }, seeds.Keys.ToArray());
        Assert.Equal(bundle.ContextMarkdown, seeds[ReviewBundle.ContextFileName]);
        Assert.Equal(bundle.ChangesMarkdown, seeds[ReviewBundle.ChangesFileName]);
        Assert.StartsWith("REVIEW/", ReviewBundle.ContextFileName);
        Assert.StartsWith("REVIEW/", ReviewBundle.ChangesFileName);
    }
}
