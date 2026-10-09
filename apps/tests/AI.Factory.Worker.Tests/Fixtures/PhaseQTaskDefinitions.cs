namespace AI.Factory.Worker.Tests.Fixtures;

/// <summary>
/// Phase Q — immutable benchmark task specifications.
/// Q-A..Q-C reuse the proven Phase F fixtures; Q-D is a new controlled
/// cross-layer (domain/application) task. Each spec carries the full delivery
/// contract: problem statement, scope, acceptance criteria, expected artifacts,
/// focused test filter and required text checks.
/// </summary>
public sealed class PhaseQTaskSpec
{
    public string TaskKey { get; init; } = string.Empty; // "Q-A" | "Q-B" | "Q-C" | "Q-D"
    public string Category { get; init; } = string.Empty;
    public BenchmarkTaskDefinition Benchmark { get; init; } = null!;
    public string AcceptanceCriteriaJson { get; init; } = string.Empty;
    public string[] RequiredFiles { get; init; } = System.Array.Empty<string>();
    public string FocusedTestFilter { get; init; } = string.Empty;
    public Dictionary<string, string> RequiredTextChecks { get; init; } = new();
}

public static class PhaseQTasks
{
    public static PhaseQTaskSpec Get(string taskKey) => taskKey switch
    {
        "Q-A" => QA(),
        "Q-B" => QB(),
        "Q-C" => QC(),
        "Q-D" => QD(),
        _ => throw new ArgumentOutOfRangeException(nameof(taskKey),
            $"Unknown Phase Q task '{taskKey}'. Expected Q-A, Q-B, Q-C or Q-D.")
    };

    public static string[] AllKeys() => new[] { "Q-A", "Q-B", "Q-C", "Q-D" };

    // Q-A — Small Feature Addition (single file + tests).
    private static PhaseQTaskSpec QA() => new()
    {
        TaskKey = "Q-A",
        Category = "SingleFile",
        Benchmark = BenchmarkTaskDefinition.TaskA_SingleFile(),
        AcceptanceCriteriaJson = """
            {"version":1,"criteria":[
              {"type":"ExpectedTextPresent","path":"src/Calculator.cs","value":"Subtract"},
              {"type":"ExpectedTextPresent","path":"src/Calculator.cs","value":"Add"},
              {"type":"ExpectedTextPresent","path":"tests/CalculatorTests.cs","value":"Subtract_ReturnsDifference"},
              {"type":"TestsPass"}
            ]}
            """,
        RequiredFiles = new[] { "src/Calculator.cs", "tests/CalculatorTests.cs" },
        FocusedTestFilter = "FullyQualifiedName~Subtract_ReturnsDifference",
        RequiredTextChecks = new Dictionary<string, string>
        {
            ["src/Calculator.cs"] = "Subtract",
            ["tests/CalculatorTests.cs"] = "Subtract_ReturnsDifference",
        },
    };

    // Q-B — Multi-File Feature (production + tests + notes).
    private static PhaseQTaskSpec QB() => new()
    {
        TaskKey = "Q-B",
        Category = "MultiFileFeature",
        Benchmark = BenchmarkTaskDefinition.TaskB_MultiFile(),
        AcceptanceCriteriaJson = """
            {"version":1,"criteria":[
              {"type":"ExpectedTextPresent","path":"src/Calculator.cs","value":"Multiply"},
              {"type":"ExpectedTextPresent","path":"tests/CalculatorTests.cs","value":"Multiply_ReturnsProduct"},
              {"type":"ExpectedTextPresent","path":"NOTES.md","value":"Multiply"},
              {"type":"TestsPass"}
            ]}
            """,
        RequiredFiles = new[] { "src/Calculator.cs", "tests/CalculatorTests.cs", "NOTES.md" },
        FocusedTestFilter = "FullyQualifiedName~Multiply_ReturnsProduct",
        RequiredTextChecks = new Dictionary<string, string>
        {
            ["src/Calculator.cs"] = "Multiply",
            ["tests/CalculatorTests.cs"] = "Multiply_ReturnsProduct",
            ["NOTES.md"] = "Multiply",
        },
    };

    // Q-C — Bug Fix / Regression (seeded Divide defect).
    private static PhaseQTaskSpec QC() => new()
    {
        TaskKey = "Q-C",
        Category = "BugFix",
        Benchmark = BenchmarkTaskDefinition.TaskC_BugFix(),
        AcceptanceCriteriaJson = """
            {"version":1,"criteria":[
              {"type":"ExpectedTextPresent","path":"src/Calculator.cs","value":"Divide"},
              {"type":"ExpectedTextPresent","path":"tests/CalculatorTests.cs","value":"Divide_ReturnsQuotient"},
              {"type":"TestsPass"}
            ]}
            """,
        // Only the production fix is strictly required; a test change is
        // acceptable-incidental (regression test addition) but not mandatory
        // because the baseline already contains the failing Divide test.
        RequiredFiles = new[] { "src/Calculator.cs" },
        FocusedTestFilter = "FullyQualifiedName~Divide_ReturnsQuotient",
        RequiredTextChecks = new Dictionary<string, string>
        {
            ["tests/CalculatorTests.cs"] = "Divide_ReturnsQuotient",
        },
    };

    // Q-D — Cross-Layer Engineering (domain + application).
    // Domain: src/Domain/Calculator.cs. Application: src/Application/CalculatorService.cs.
    // The agent must coordinate both layers plus tests.
    private static PhaseQTaskSpec QD()
    {
        var benchmark = new BenchmarkTaskDefinition
        {
            TaskId = "BENCH-D-CROSSLAYER",
            Category = "CrossLayer",
            Title = "Implement Multiply across domain and application layers",
            Description =
                "In src/Domain/Calculator.cs, add a Multiply method: `public int Multiply(int a, int b) => a * b;`.\n" +
                "In src/Application/CalculatorService.cs, implement `public int MultiplyAndAdd(int a, int b, int c)` to return `a * b + c` using the domain Calculator (do not hard-code arithmetic without calling Calculator).\n" +
                "Ensure all tests in tests/CalculatorServiceTests.cs pass.\n" +
                "Do not delete or weaken any existing tests.\n" +
                "Do not modify any other file.",
            SetupBaselineAction = seedDir =>
            {
                var srcDomain = Path.Combine(seedDir, "src", "Domain");
                var srcApp = Path.Combine(seedDir, "src", "Application");
                var testsDir = Path.Combine(seedDir, "tests");
                Directory.CreateDirectory(srcDomain);
                Directory.CreateDirectory(srcApp);
                Directory.CreateDirectory(testsDir);

                File.WriteAllText(Path.Combine(seedDir, "Demo.slnx"), """
                    <Solution>
                      <Project Path="src/Demo.csproj" />
                      <Project Path="tests/Demo.Tests.csproj" />
                    </Solution>
                    """);

                File.WriteAllText(Path.Combine(seedDir, "src", "Demo.csproj"), """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """);

                File.WriteAllText(Path.Combine(srcDomain, "Calculator.cs"), """
                    namespace Demo.Domain;

                    public class Calculator
                    {
                        public int Add(int a, int b) => a + b;
                    }
                    """);

                File.WriteAllText(Path.Combine(srcApp, "CalculatorService.cs"), """
                    using Demo.Domain;

                    namespace Demo.Application;

                    public class CalculatorService
                    {
                        private readonly Calculator _calculator = new();

                        public int Add(int a, int b) => _calculator.Add(a, b);

                        // TODO: should return a * b + c using the domain Calculator.
                        public int MultiplyAndAdd(int a, int b, int c) => 0;
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

                File.WriteAllText(Path.Combine(testsDir, "CalculatorServiceTests.cs"), """
                    using Demo.Application;
                    using Xunit;

                    namespace Demo.Tests;

                    public class CalculatorServiceTests
                    {
                        [Fact]
                        public void Add_ReturnsSum()
                        {
                            var service = new CalculatorService();
                            Assert.Equal(5, service.Add(2, 3));
                        }

                        [Fact]
                        public void MultiplyAndAdd_ReturnsExpected()
                        {
                            var service = new CalculatorService();
                            Assert.Equal(17, service.MultiplyAndAdd(3, 4, 5));
                        }
                    }
                    """);
            },
            FileClassifier = path =>
            {
                if (path.Equals("src/Domain/Calculator.cs", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("src/Application/CalculatorService.cs", StringComparison.OrdinalIgnoreCase))
                    return "Required";
                if (path.Equals("tests/CalculatorServiceTests.cs", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("obj/") || path.Contains("bin/") || path.Contains(".git/"))
                    return "AcceptableIncidental";
                return "Unexpected";
            },
        };

        return new PhaseQTaskSpec
        {
            TaskKey = "Q-D",
            Category = "CrossLayer",
            Benchmark = benchmark,
            AcceptanceCriteriaJson = """
                {"version":1,"criteria":[
                  {"type":"ExpectedTextPresent","path":"src/Domain/Calculator.cs","value":"Multiply"},
                  {"type":"ExpectedTextPresent","path":"src/Application/CalculatorService.cs","value":"MultiplyAndAdd"},
                  {"type":"ExpectedTextPresent","path":"tests/CalculatorServiceTests.cs","value":"MultiplyAndAdd_ReturnsExpected"},
                  {"type":"TestsPass"}
                ]}
                """,
            RequiredFiles = new[]
            {
                "src/Domain/Calculator.cs",
                "src/Application/CalculatorService.cs",
            },
            FocusedTestFilter = "FullyQualifiedName~MultiplyAndAdd_ReturnsExpected",
            RequiredTextChecks = new Dictionary<string, string>
            {
                ["src/Domain/Calculator.cs"] = "Multiply",
                ["src/Application/CalculatorService.cs"] = "MultiplyAndAdd",
                ["tests/CalculatorServiceTests.cs"] = "MultiplyAndAdd_ReturnsExpected",
            },
        };
    }
}
