using AI.Factory.Application.Orchestration;
using AI.Factory.Infrastructure.Agents;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class SecurityBoundaryTests
{
    [Fact]
    public void WorkspacePathGuard_Throws_ForDotDotPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-factory-security-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        Assert.Throws<SecurityViolationException>(() =>
            WorkspacePathGuard.EnsureWithinRoot(root, Path.Combine("..", "..", "escaped.txt")));
    }

    [Fact]
    public void WorkspacePathGuard_Throws_ForAbsolutePathEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-factory-security-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var absoluteEscape = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\config"
            : "/etc/passwd";

        Assert.Throws<SecurityViolationException>(() =>
            WorkspacePathGuard.EnsureWithinRoot(root, absoluteEscape));
    }

    [Fact]
    public void WorkspacePathGuard_Succeeds_ForPathWithinWorkspaceBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-factory-security-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var resolved = WorkspacePathGuard.EnsureWithinRoot(root, "subdir/file.txt");

        Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkspacePathGuard_Throws_WhenRootIsNotConfigured()
    {
        Assert.Throws<SecurityViolationException>(() =>
            WorkspacePathGuard.EnsureWithinRoot(string.Empty, "anything"));
    }

    [Fact]
    public async Task SystemProcessRunner_Throws_ForExplicitlyDeniedCommand()
    {
        var runner = new SystemProcessRunner(new CommandPolicyOptions
        {
            AllowedCommands = new[] { "rm" },
            DeniedCommands = new[] { "rm" }
        });

        await Assert.ThrowsAsync<SecurityViolationException>(() =>
            runner.RunAsync(new ProcessRunRequest("rm", new[] { "-rf", "/" }, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task SystemProcessRunner_Throws_ForCommandNotInAllowList()
    {
        var runner = new SystemProcessRunner(new CommandPolicyOptions
        {
            AllowedCommands = new[] { "docker" },
            DeniedCommands = Array.Empty<string>()
        });

        await Assert.ThrowsAsync<SecurityViolationException>(() =>
            runner.RunAsync(new ProcessRunRequest("some-random-tool", Array.Empty<string>(), TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task SystemProcessRunner_AllowsExplicitlyAllowedCommand_ToProceedPastPolicyCheck()
    {
        var runner = new SystemProcessRunner(new CommandPolicyOptions
        {
            AllowedCommands = new[] { "cmd" },
            DeniedCommands = Array.Empty<string>()
        });

        // Allowed by policy → proceeds to actually launch; asserting only
        // that it is NOT rejected as a SecurityViolationException proves
        // the allow-list let it through the boundary check.
        var result = await runner.RunAsync(
            new ProcessRunRequest("cmd.exe", new[] { "/c", "exit", "0" }, TimeSpan.FromSeconds(10)));

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task SystemProcessRunner_DefaultPolicy_DeniesDangerousCommands_WithoutExplicitConfiguration()
    {
        var runner = new SystemProcessRunner();

        await Assert.ThrowsAsync<SecurityViolationException>(() =>
            runner.RunAsync(new ProcessRunRequest("powershell", Array.Empty<string>(), TimeSpan.FromSeconds(5))));
    }
}
