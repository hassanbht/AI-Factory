using System.Diagnostics;

namespace AI.Factory.Worker.Tests.Fixtures;

/// <summary>
/// Phase 18.18 — Canonical small .NET solution fixture for deterministic
/// end-to-end acceptance testing. Creates a clean, fast, isolated Git
/// repository containing a compilable C# library and test project.
/// </summary>
public static class GoldenProjectFixture
{
    public static string CreateBareRepo(string tempDir)
    {
        var seedDir = Path.Combine(tempDir, "seed");
        Directory.CreateDirectory(seedDir);

        var srcDir = Path.Combine(seedDir, "src");
        var testsDir = Path.Combine(seedDir, "tests");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(testsDir);

        // src/Demo.csproj
        File.WriteAllText(Path.Combine(srcDir, "Demo.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        // src/GreetingService.cs
        File.WriteAllText(Path.Combine(srcDir, "GreetingService.cs"), """
            namespace Demo;

            public class GreetingService
            {
                public string GetGreeting() => "Hello";
            }
            """);

        // tests/Demo.Tests.csproj
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

        // tests/GreetingServiceTests.cs
        File.WriteAllText(Path.Combine(testsDir, "GreetingServiceTests.cs"), """
            using Xunit;

            namespace Demo.Tests;

            public class GreetingServiceTests
            {
                [Fact]
                public void Greeting_IsNotEmpty()
                {
                    var service = new GreetingService();
                    Assert.NotEmpty(service.GetGreeting());
                }
            }
            """);

        // Solution file at repository root so dotnet build / dotnet test find the projects
        File.WriteAllText(Path.Combine(seedDir, "Demo.slnx"), """
            <Solution>
              <Project Path="src/Demo.csproj" />
              <Project Path="tests/Demo.Tests.csproj" />
            </Solution>
            """);

        RunGit(seedDir, "init", "-b", "main");
        RunGit(seedDir, "config", "user.email", "test@example.com");
        RunGit(seedDir, "config", "user.name", "Test");
        RunGit(seedDir, "config", "commit.gpgsign", "false");
        RunGit(seedDir, "add", ".");
        RunGit(seedDir, "commit", "-m", "initial commit");

        var bareRepoPath = Path.Combine(tempDir, "golden.git");
        RunGit(tempDir, "clone", "--bare", seedDir, bareRepoPath);

        return bareRepoPath;
    }

    /// <summary>
    /// Modifies src/GreetingService.cs to return "Hello, AI Factory!".
    /// </summary>
    public static void ApplyT02ImplementationChange(string workspaceRoot)
    {
        var file = Path.Combine(workspaceRoot, "src", "GreetingService.cs");
        File.WriteAllText(file, """
            namespace Demo;

            public class GreetingService
            {
                public string GetGreeting() => "Hello, AI Factory!";
            }
            """);
    }

    /// <summary>
    /// Injects an intentional test defect in tests/GreetingServiceTests.cs
    /// asserting an expected value that GreetingService does not currently return.
    /// This causes dotnet test to fail, triggering the mandatory Repair loop.
    /// </summary>
    public static void InjectT03TestDefect(string workspaceRoot)
    {
        var file = Path.Combine(workspaceRoot, "tests", "GreetingServiceTests.cs");
        File.WriteAllText(file, """
            using Xunit;

            namespace Demo.Tests;

            public class GreetingServiceTests
            {
                [Fact]
                public void Greeting_MatchesExpectedGold()
                {
                    var service = new GreetingService();
                    // Intentional defect: asserts "Hello, Repaired AI Factory!" which will fail
                    Assert.Equal("Hello, Repaired AI Factory!", service.GetGreeting());
                }
            }
            """);
    }

    /// <summary>
    /// Applies the repair fix to src/GreetingService.cs so the assertion in T03 passes.
    /// </summary>
    public static void ApplyT03Repair(string workspaceRoot)
    {
        var file = Path.Combine(workspaceRoot, "src", "GreetingService.cs");
        File.WriteAllText(file, """
            namespace Demo;

            public class GreetingService
            {
                public string GetGreeting() => "Hello, Repaired AI Factory!";
            }
            """);
    }

    /// <summary>
    /// Applies final integration addition in src/GreetingService.cs and tests.
    /// </summary>
    public static void ApplyT04FinalIntegration(string workspaceRoot)
    {
        var srcFile = Path.Combine(workspaceRoot, "src", "GreetingService.cs");
        File.WriteAllText(srcFile, """
            namespace Demo;

            public class GreetingService
            {
                public string GetGreeting() => "Hello, Repaired AI Factory!";
                public bool IsIntegrated() => true;
            }
            """);

        var testFile = Path.Combine(workspaceRoot, "tests", "GreetingServiceTests.cs");
        File.WriteAllText(testFile, """
            using Xunit;

            namespace Demo.Tests;

            public class GreetingServiceTests
            {
                [Fact]
                public void Greeting_MatchesExpectedGold()
                {
                    var service = new GreetingService();
                    Assert.Equal("Hello, Repaired AI Factory!", service.GetGreeting());
                }

                [Fact]
                public void Service_IsIntegrated()
                {
                    var service = new GreetingService();
                    Assert.True(service.IsIntegrated());
                }
            }
            """);
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
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed:\n{process.StandardError.ReadToEnd()}");
        }
    }
}
