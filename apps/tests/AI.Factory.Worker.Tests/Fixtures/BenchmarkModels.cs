using System.Text.Json;
using System.Text.Json.Serialization;

namespace AI.Factory.Worker.Tests.Fixtures;

/// <summary>
/// Phase F — Benchmark data models for empirical Hermes vs OpenHands comparison.
/// Explicit, provider-neutral comparison record outside the production database schema.
/// </summary>
public sealed class BenchmarkRunRecord
{
    public string BenchmarkId { get; set; } = string.Empty;
    public string? TaskCategory { get; set; }
    public string? TaskTitle { get; set; }
    public string Runtime { get; set; } = string.Empty; // "Hermes" | "OpenHands"
    public Guid RunId { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid ProjectId { get; set; }
    public string SourceCommit { get; set; } = string.Empty;
    public string BaselineHash { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public double ExecutionDurationSeconds { get; set; }
    public string TerminalStatus { get; set; } = string.Empty;
    public string? FailureCategory { get; set; }
    public string? ExitReason { get; set; }
    public int AttemptNumber { get; set; } = 1;
    public int RetryCount { get; set; }
    public int RepairCount { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public string AcceptanceStatus { get; set; } = "NotRun"; // "Passed" | "Failed"
    public double VerificationDurationSeconds { get; set; }
    public string ReviewStatus { get; set; } = "NotRun";
    public List<string> ChangedFiles { get; set; } = new();
    public Dictionary<string, string> FileClassifications { get; set; } = new(); // Required, AcceptableIncidental, Unexpected, Harmful
    public int ArtifactCount { get; set; }
    public bool CleanupVerified { get; set; }
    public string DiffSummary { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static BenchmarkRunRecord? FromJson(string json) =>
        JsonSerializer.Deserialize<BenchmarkRunRecord>(json, JsonOptions);
}

public sealed class BenchmarkTaskDefinition
{
    public string TaskId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    [JsonIgnore]
    public Action<string>? SetupBaselineAction { get; set; }

    [JsonIgnore]
    public Func<string, string>? FileClassifier { get; set; }

    public static BenchmarkTaskDefinition TaskA_SingleFile() => new()
    {
        TaskId = "BENCH-A-SINGLEFILE",
        Category = "SingleFile",
        Title = "Implement Subtract in Calculator with Unit Test",
        Description =
            "In src/Calculator.cs, add a Subtract method: `public int Subtract(int a, int b) => a - b;`.\n" +
            "In tests/CalculatorTests.cs, add a unit test `Subtract_ReturnsDifference` verifying `new Calculator().Subtract(10, 4) == 6`.\n" +
            "Do not modify any other file.",
        SetupBaselineAction = seedDir =>
        {
            var srcDir = Path.Combine(seedDir, "src");
            var testsDir = Path.Combine(seedDir, "tests");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(testsDir);

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

            File.WriteAllText(Path.Combine(srcDir, "Calculator.cs"), """
                namespace Demo;

                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
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

            File.WriteAllText(Path.Combine(testsDir, "CalculatorTests.cs"), """
                using Xunit;

                namespace Demo.Tests;

                public class CalculatorTests
                {
                    [Fact]
                    public void Add_ReturnsSum()
                    {
                        var calc = new Calculator();
                        Assert.Equal(5, calc.Add(2, 3));
                    }
                }
                """);
        },
        FileClassifier = path =>
        {
            if (path.Equals("src/Calculator.cs", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("tests/CalculatorTests.cs", StringComparison.OrdinalIgnoreCase))
                return "Required";
            if (path.Contains("obj/") || path.Contains("bin/") || path.Contains(".git/"))
                return "AcceptableIncidental";
            return "Unexpected";
        }
    };

    public static BenchmarkTaskDefinition TaskB_MultiFile() => new()
    {
        TaskId = "BENCH-B-MULTIFILE",
        Category = "MultiFileFeature",
        Title = "Implement Multiply in Calculator with Unit Tests and Update Notes",
        Description =
            "In src/Calculator.cs, add a Multiply method: `public int Multiply(int a, int b) => a * b;`.\n" +
            "In tests/CalculatorTests.cs, add a unit test `Multiply_ReturnsProduct` verifying `new Calculator().Multiply(3, 4) == 12`.\n" +
            "In NOTES.md, append `- Multiply` to the Features list.\n" +
            "Do not modify any other file.",
        SetupBaselineAction = seedDir =>
        {
            var srcDir = Path.Combine(seedDir, "src");
            var testsDir = Path.Combine(seedDir, "tests");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(testsDir);

            File.WriteAllText(Path.Combine(seedDir, "Demo.slnx"), """
                <Solution>
                  <Project Path="src/Demo.csproj" />
                  <Project Path="tests/Demo.Tests.csproj" />
                </Solution>
                """);

            File.WriteAllText(Path.Combine(seedDir, "NOTES.md"), """
                # Demo Calculator Notes
                Version: 1.0.0

                Features:
                - Add
                """);

            File.WriteAllText(Path.Combine(srcDir, "Demo.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(srcDir, "Calculator.cs"), """
                namespace Demo;

                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
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

            File.WriteAllText(Path.Combine(testsDir, "CalculatorTests.cs"), """
                using Xunit;

                namespace Demo.Tests;

                public class CalculatorTests
                {
                    [Fact]
                    public void Add_ReturnsSum()
                    {
                        var calc = new Calculator();
                        Assert.Equal(5, calc.Add(2, 3));
                    }
                }
                """);
        },
        FileClassifier = path =>
        {
            if (path.Equals("src/Calculator.cs", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("tests/CalculatorTests.cs", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("NOTES.md", StringComparison.OrdinalIgnoreCase))
                return "Required";
            if (path.Contains("obj/") || path.Contains("bin/") || path.Contains(".git/"))
                return "AcceptableIncidental";
            return "Unexpected";
        }
    };

    public static BenchmarkTaskDefinition TaskC_BugFix() => new()
    {
        TaskId = "BENCH-C-BUGFIX",
        Category = "BugFix",
        Title = "Fix Buggy Divide Method in Calculator",
        Description =
            "In src/Calculator.cs, the Divide method is buggy and returns `a + b` instead of `a / b`.\n" +
            "Fix `public int Divide(int a, int b)` in src/Calculator.cs to correctly perform integer division `a / b`.\n" +
            "Ensure that existing tests in tests/CalculatorTests.cs pass.\n" +
            "Do not delete, weaken, or skip any existing unit tests.\n" +
            "Do not modify any other file.",
        SetupBaselineAction = seedDir =>
        {
            var srcDir = Path.Combine(seedDir, "src");
            var testsDir = Path.Combine(seedDir, "tests");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(testsDir);

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

            File.WriteAllText(Path.Combine(srcDir, "Calculator.cs"), """
                namespace Demo;

                public class Calculator
                {
                    public int Add(int a, int b) => a + b;

                    // Buggy method: uses + instead of /
                    public int Divide(int a, int b) => a + b;
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

            File.WriteAllText(Path.Combine(testsDir, "CalculatorTests.cs"), """
                using Xunit;

                namespace Demo.Tests;

                public class CalculatorTests
                {
                    [Fact]
                    public void Add_ReturnsSum()
                    {
                        var calc = new Calculator();
                        Assert.Equal(5, calc.Add(2, 3));
                    }

                    [Fact]
                    public void Divide_ReturnsQuotient()
                    {
                        var calc = new Calculator();
                        Assert.Equal(5, calc.Divide(10, 2));
                    }
                }
                """);
        },
        FileClassifier = path =>
        {
            if (path.Equals("src/Calculator.cs", StringComparison.OrdinalIgnoreCase))
                return "Required";
            if (path.Equals("tests/CalculatorTests.cs", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("obj/") || path.Contains("bin/") || path.Contains(".git/"))
                return "AcceptableIncidental";
            return "Unexpected";
        }
    };

    public static BenchmarkTaskDefinition TaskD_TestRepair() => new()
    {
        TaskId = "BENCH-D-TESTREPAIR",
        Category = "TestRepair",
        Title = "Repair GreetingFormatter to Satisfy Specification Tests",
        Description =
            "In src/GreetingFormatter.cs, the FormatGreeting method currently returns `Hello, {name}!` which fails specification tests.\n" +
            "Update `public string FormatGreeting(string? name)` in src/GreetingFormatter.cs to return `\"Welcome, Guest!\"` if name is null, empty, or whitespace, and `\"Welcome, \" + name + \"!\"` otherwise.\n" +
            "Ensure all tests in tests/GreetingTests.cs pass.\n" +
            "Do not delete or weaken any tests.\n" +
            "Do not modify any other file.",
        SetupBaselineAction = seedDir =>
        {
            var srcDir = Path.Combine(seedDir, "src");
            var testsDir = Path.Combine(seedDir, "tests");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(testsDir);

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

            File.WriteAllText(Path.Combine(srcDir, "GreetingFormatter.cs"), """
                namespace Demo;

                public class GreetingFormatter
                {
                    // Failing implementation: does not handle null/empty and uses wrong greeting prefix
                    public string FormatGreeting(string? name) => $"Hello, {name}!";
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

            File.WriteAllText(Path.Combine(testsDir, "GreetingTests.cs"), """
                using Xunit;

                namespace Demo.Tests;

                public class GreetingTests
                {
                    [Fact]
                    public void FormatGreeting_WithName_ReturnsWelcomeName()
                    {
                        var formatter = new GreetingFormatter();
                        Assert.Equal("Welcome, Alice!", formatter.FormatGreeting("Alice"));
                    }

                    [Fact]
                    public void FormatGreeting_WithNullOrWhitespace_ReturnsWelcomeGuest()
                    {
                        var formatter = new GreetingFormatter();
                        Assert.Equal("Welcome, Guest!", formatter.FormatGreeting(null));
                        Assert.Equal("Welcome, Guest!", formatter.FormatGreeting(""));
                        Assert.Equal("Welcome, Guest!", formatter.FormatGreeting("   "));
                    }
                }
                """);
        },
        FileClassifier = path =>
        {
            if (path.Equals("src/GreetingFormatter.cs", StringComparison.OrdinalIgnoreCase))
                return "Required";
            if (path.Equals("tests/GreetingTests.cs", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("obj/") || path.Contains("bin/") || path.Contains(".git/"))
                return "AcceptableIncidental";
            return "Unexpected";
        }
    };
}

public sealed class BenchmarkComparisonSummary
{
    public string BenchmarkId { get; set; } = "BENCH-001";
    public string Model { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public string CanonicalSourceCommit { get; set; } = string.Empty;
    public string Validity { get; set; } = "CONTROLLED"; // "CONTROLLED" | "LIMITED" | "INVALID"
    public string ValidityReason { get; set; } = string.Empty;

    public List<BenchmarkRunRecord> HermesRuns { get; set; } = new();
    public List<BenchmarkRunRecord> OpenHandsRuns { get; set; } = new();

    public static double CalculateMedian(IEnumerable<double> values)
    {
        var list = values.OrderBy(v => v).ToList();
        if (list.Count == 0) return 0;
        int mid = list.Count / 2;
        return (list.Count % 2 != 0) ? list[mid] : (list[mid - 1] + list[mid]) / 2.0;
    }
}
