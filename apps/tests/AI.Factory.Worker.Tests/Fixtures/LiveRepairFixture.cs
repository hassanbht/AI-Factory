using System.Diagnostics;

namespace AI.Factory.Worker.Tests.Fixtures;

/// <summary>
/// Phase 18.20 §12 — the deterministic repair fixture for the live
/// (real Hermes) acceptance tests.
///
/// The whole point is that the verification failure is guaranteed by
/// <em>construction</em>, not by hoping a weak live model chooses to write a
/// bug. <c>tests/GreetingServiceTests.cs</c> asserts the exact string
/// <c>"Hello"</c>, so the moment the implementation agent carries out its
/// instruction ("make GetGreeting() return 'Hello, AI Factory!'") the existing
/// test necessarily fails, a real <c>dotnet test</c> run reports it, and the
/// WorkItem enters Repairing through the ordinary pipeline — no status is ever
/// set by hand (§11).
///
/// The repair is then equally forgiving: the repair agent may either update the
/// test's expectation or revert the implementation; both make the suite green,
/// so the scenario does not depend on the model picking one particular fix.
/// </summary>
public static class LiveRepairFixture
{
    public const string OriginalGreeting = "Hello";
    public const string RequestedGreeting = "Hello, AI Factory!";

    /// <summary>Creates a seed repo, commits it, and returns the path of a bare clone to use as the Project's Git source.</summary>
    public static string CreateBareRepo(string tempDir)
    {
        var seedDir = Path.Combine(tempDir, "seed");
        var srcDir = Path.Combine(seedDir, "src");
        var testsDir = Path.Combine(seedDir, "tests");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(testsDir);

        // Required so `dotnet build`/`dotnet test` against the workspace ROOT
        // (what IVerificationGate always targets) can resolve a project —
        // without it MSBuild fails with MSB1003 (Phase 18.19 §4.3).
        File.WriteAllText(Path.Combine(seedDir, "Demo.slnx"), """
            <Solution>
              <Project Path="src/Demo.csproj" />
              <Project Path="tests/Demo.Tests.csproj" />
            </Solution>
            """);

        File.WriteAllText(Path.Combine(srcDir, "Demo.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(srcDir, "GreetingService.cs"), """
            namespace Demo;

            public class GreetingService
            {
                public string GetGreeting() => "Hello";
            }
            """);

        File.WriteAllText(Path.Combine(testsDir, "Demo.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2">
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                  <PrivateAssets>all</PrivateAssets>
                </PackageReference>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                <ProjectReference Include="../src/Demo.csproj" />
              </ItemGroup>
            </Project>
            """);

        // Asserts the EXACT current greeting — this is what makes the first
        // implementation WorkItem deterministically break the build/test gate.
        File.WriteAllText(Path.Combine(testsDir, "GreetingServiceTests.cs"), """
            using Xunit;

            namespace Demo.Tests;

            public class GreetingServiceTests
            {
                [Fact]
                public void GetGreeting_ReturnsExpectedGreeting()
                {
                    Assert.Equal("Hello", new GreetingService().GetGreeting());
                }
            }
            """);

        RunGit(seedDir, "init", "-b", "main");
        RunGit(seedDir, "config", "user.email", "test@example.com");
        RunGit(seedDir, "config", "user.name", "Test");
        RunGit(seedDir, "config", "commit.gpgsign", "false");
        RunGit(seedDir, "add", ".");
        RunGit(seedDir, "commit", "-m", "initial commit");

        var bareRepoPath = Path.Combine(tempDir, "live-repair.git");
        RunGit(tempDir, "clone", "--bare", seedDir, bareRepoPath);

        return bareRepoPath;
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

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(30_000);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed:\n{process.StandardError.ReadToEnd()}");
        }
    }
}
