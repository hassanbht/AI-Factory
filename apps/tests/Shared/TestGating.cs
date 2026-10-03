using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

// Phase O — compiled into every test project (linked file), so the three assemblies share ONE definition of
// "this test needs something the default run must never touch".
namespace AI.Factory.TestSupport;

/// <summary>
/// Phase O — the single place that decides whether a test that talks to something real (a billed model
/// provider, a real model-backed CLI, the Docker daemon) may run. The rules:
/// <list type="bullet">
/// <item>The default <c>dotnet test</c> makes no provider call and loads no <c>.env.live</c>.</item>
/// <item>A live test runs only when one of its opt-in variables is set explicitly in the process environment.</item>
/// <item>A live test that is not enabled, or whose credential/Docker is missing, is reported by xunit as
/// SKIPPED with a reason — never as a pass.</item>
/// </list>
/// The reason text always starts with the evidence class (<c>SKIPPED — LIVE OPT-IN NOT ENABLED</c> or
/// <c>ENVIRONMENT BLOCKED</c>) and never contains a variable's value.
/// </summary>
public static class LiveTestGate
{
    /// <summary>Every opt-in variable a Category D/E test (real CLI contract, real provider) is gated by.</summary>
    public static readonly IReadOnlyList<string> KnownOptInVariables = new[]
    {
        "AI_FACTORY_HERMES_SMOKE_TEST",
        "AI_FACTORY_OPENHANDS_SMOKE_TEST",
        "AI_FACTORY_LIVE_HERMES_CREDENTIALS",
        "AI_FACTORY_LIVE_GATEWAY_AGENTRUN",
        "AI_FACTORY_LIVE_SMOKE",
        "AI_FACTORY_LIVE_CLAUDE_CONTRACT",
        "AI_FACTORY_LIVE_BENCHMARK",
        "AI_FACTORY_RUN_BENCHMARK",
        "AI_FACTORY_LIVE_CERT_HERMES",
        "AI_FACTORY_LIVE_CERT_OPENHANDS"
    };

    /// <summary>"Enabled" is any value other than empty / 0 / false / no / off (the project uses both <c>true</c> and <c>1</c>, and a runtime name for AI_FACTORY_LIVE_SMOKE).</summary>
    public static bool IsEnabled(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && trimmed != "0"
            && !trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("no", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    public static bool AnyKnownOptInEnabled(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        return KnownOptInVariables.Any(name => IsEnabled(getEnvironmentVariable(name)));
    }

    /// <summary>
    /// Null when the test may run; otherwise the xunit skip reason. <paramref name="loadEnvLive"/> runs only once an
    /// opt-in is present AND the test declares required environment (so a gitignored <c>.env.live</c> can supply a
    /// credential for a run the user asked for, and is never read otherwise).
    /// </summary>
    public static string? Evaluate(
        IReadOnlyCollection<string> optInVariables,
        IReadOnlyCollection<string> requiredEnvironment,
        bool requiresDocker,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<bool>? dockerAvailable = null,
        Action? loadEnvLive = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        if (!optInVariables.Any(name => IsEnabled(getEnvironmentVariable(name))))
        {
            return "SKIPPED — LIVE OPT-IN NOT ENABLED (set " + string.Join(" or ", optInVariables) + ")";
        }

        // Least privilege: the gitignored .env.live is read only for a test that declares it needs something from the environment.
        if (requiredEnvironment.Count > 0)
        {
            loadEnvLive?.Invoke();
        }

        var missing = requiredEnvironment.Where(name => string.IsNullOrWhiteSpace(getEnvironmentVariable(name))).ToArray();
        if (missing.Length > 0)
        {
            return "ENVIRONMENT BLOCKED — opt-in enabled but required credential variable(s) not set: " + string.Join(", ", missing);
        }

        if (requiresDocker && !(dockerAvailable ?? DockerProbe.DaemonReachable)())
        {
            return "ENVIRONMENT BLOCKED — Docker daemon not reachable";
        }

        return null;
    }
}

/// <summary>Phase O — the guarded loader for the gitignored <c>.env.live</c>. It reads nothing unless a live opt-in is set.</summary>
public static class LiveEnvironment
{
    /// <returns>The NAMES of the variables set from the file (never their values).</returns>
    public static IReadOnlyList<string> TryLoadEnvLive(string? startDirectory = null, bool? optedIn = null)
    {
        if (!(optedIn ?? LiveTestGate.AnyKnownOptInEnabled()))
        {
            return Array.Empty<string>();
        }

        var loaded = new List<string>();

        try
        {
            var dir = startDirectory ?? AppContext.BaseDirectory;
            while (dir is not null)
            {
                var candidate = Path.Combine(dir, ".env.live");
                if (File.Exists(candidate))
                {
                    foreach (var line in File.ReadAllLines(candidate))
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                        {
                            continue;
                        }

                        var idx = trimmed.IndexOf('=');
                        if (idx <= 0)
                        {
                            continue;
                        }

                        var key = trimmed[..idx].Trim();
                        var value = trimmed[(idx + 1)..].Trim();

                        // The file never switches a live test on: opt-ins come from the real environment only.
                        if (key.Contains("SMOKE_TEST", StringComparison.OrdinalIgnoreCase)
                            || LiveTestGate.KnownOptInVariables.Contains(key, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                        {
                            Environment.SetEnvironmentVariable(key, value);
                            loaded.Add(key);
                        }
                    }

                    break;
                }

                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable file simply supplies nothing; the credential check then reports ENVIRONMENT BLOCKED.
        }

        return loaded;
    }
}

internal static class DockerProbe
{
    private static readonly Lazy<bool> Daemon = new(() => Probe("info"));
    private static readonly Lazy<bool> Cli = new(() => Probe("--version"));

    public static bool DaemonReachable() => Daemon.Value;

    public static bool CliPresent() => Cli.Value;

    private static bool Probe(string argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", argument)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return false;
            }

            // Drained so a full pipe can never stall `docker info`.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// A test that talks to something real and billed/model-backed (Category E) or to a real CLI against a scripted endpoint
/// (Category D, opt-in): skipped unless one of <see cref="OptInVariables"/> is set; skipped as ENVIRONMENT BLOCKED when a
/// listed credential, or Docker, is missing.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(params string[] optInVariables) => OptInVariables = optInVariables;

    public string[] OptInVariables { get; }

    /// <summary>Environment variable NAMES that must be set (by the caller or, once opted in, by .env.live) — credentials, endpoint URLs, executable paths.</summary>
    public string[] RequiredEnvironment { get; set; } = Array.Empty<string>();

    public bool RequiresDocker { get; set; }

    public override string? Skip
    {
        get => base.Skip ?? LiveTestGate.Evaluate(
            OptInVariables, RequiredEnvironment, RequiresDocker, loadEnvLive: () => LiveEnvironment.TryLoadEnvLive(optedIn: true));
        set => base.Skip = value;
    }
}

/// <summary><see cref="LiveFactAttribute"/> for a theory.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute(params string[] optInVariables) => OptInVariables = optInVariables;

    public string[] OptInVariables { get; }

    public string[] RequiredEnvironment { get; set; } = Array.Empty<string>();

    public bool RequiresDocker { get; set; }

    public override string? Skip
    {
        get => base.Skip ?? LiveTestGate.Evaluate(
            OptInVariables, RequiredEnvironment, RequiresDocker, loadEnvLive: () => LiveEnvironment.TryLoadEnvLive(optedIn: true));
        set => base.Skip = value;
    }
}

/// <summary>A local-integration test (Category C) that needs Docker but no model and no credential: ENVIRONMENT BLOCKED, not a pass, when Docker is absent.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DockerFactAttribute : FactAttribute
{
    /// <summary>False when only the docker CLI is needed (<c>docker compose config</c>), not a running daemon.</summary>
    public bool RequiresDaemon { get; set; } = true;

    public override string? Skip
    {
        get => base.Skip ?? (RequiresDaemon
            ? (DockerProbe.DaemonReachable() ? null : "ENVIRONMENT BLOCKED — Docker daemon not reachable")
            : (DockerProbe.CliPresent() ? null : "ENVIRONMENT BLOCKED — docker CLI not found"));
        set => base.Skip = value;
    }
}

/// <summary>A test that exercises Windows process behaviour: skipped, not passed, anywhere else.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public override string? Skip
    {
        get => base.Skip ?? (OperatingSystem.IsWindows() ? null : "SKIPPED — Windows-only test");
        set => base.Skip = value;
    }
}

/// <summary><see cref="WindowsFactAttribute"/> for a theory.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public override string? Skip
    {
        get => base.Skip ?? (OperatingSystem.IsWindows() ? null : "SKIPPED — Windows-only test");
        set => base.Skip = value;
    }
}

/// <summary>
/// Phase O — the two serialized collections. Everything else keeps xunit's default parallelism.
/// <list type="bullet">
/// <item><see cref="RealProcesses"/> — tests that start real child processes (a fake CLI with a grandchild, a shell, the real
/// claude.exe) or mutate process-wide environment variables: one at a time within their assembly, after the parallel
/// collections have finished.</item>
/// <item><see cref="DockerAndBuilds"/> — tests that start Docker containers or run real <c>dotnet build</c>/<c>test</c>: the
/// bottleneck they contend for (the Docker daemon, CPU and memory for MSBuild) is shared by ALL THREE test assemblies, which
/// <c>dotnet test AI.Factory.slnx</c> runs concurrently, so these also hold a machine-wide lock (see
/// <see cref="MachineWideLockFixture"/>): one such test at a time on the whole machine.</item>
/// </list>
/// Measured on a 16 GB host with ~3 GB free: the Docker cancellation test, the golden-lifecycle E2E and the live Hermes review
/// each failed intermittently when they overlapped (Phase N F6-F8); the longest of them is the golden lifecycle (about 90 s).
/// </summary>
public static class TestCollections
{
    public const string RealProcesses = "Real processes (serialized per assembly)";
    public const string DockerAndBuilds = "Docker and real builds (serialized machine-wide)";
}

[CollectionDefinition(TestCollections.RealProcesses, DisableParallelization = true)]
public sealed class RealProcessesCollection
{
}

[CollectionDefinition(TestCollections.DockerAndBuilds, DisableParallelization = true)]
public sealed class DockerAndBuildsCollection : ICollectionFixture<MachineWideLockFixture>
{
}

/// <summary>
/// Held for the whole <see cref="TestCollections.DockerAndBuilds"/> collection: an exclusive handle on a file in the temp
/// directory. A second test assembly (another process) waits for it; if the holder dies the OS releases it, so a crashed run
/// can never leave the lock behind. Waiting is bounded: a test that cannot get the lock fails with a clear message rather than
/// running unprotected.
/// </summary>
public sealed class MachineWideLockFixture : IDisposable
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    private readonly FileStream _handle;

    public MachineWideLockFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), "ai-factory-tests-docker-and-builds.lock");
        var started = DateTime.UtcNow;

        while (true)
        {
            try
            {
                _handle = new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                return;
            }
            catch (IOException) when (DateTime.UtcNow - started < MaxWait)
            {
                Thread.Sleep(250); // another test assembly is inside this collection
            }
            catch (IOException ex)
            {
                throw new TimeoutException(
                    $"Another test process held the Docker/build lock for more than {MaxWait.TotalMinutes:F0} minutes.", ex);
            }
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// Phase O — regression guard for the gate itself. Compiled into each test assembly; a new live-shaped test that uses a
/// plain <c>[Fact]</c> (and would therefore "pass" by returning early, or run billed by accident) fails here.
/// </summary>
public sealed class LiveTestGateTests
{
    // Types whose name looks live, with the methods that are NOT live (no provider, no model, no daemon) and so run by default.
    private static readonly HashSet<string> NonLiveMethodsInLiveNamedTypes = new(StringComparer.Ordinal)
    {
        "ModelGatewayLiveTests.DisposableAgentRun_RoutesProfileAndModelRoute_ToASyntheticAdapter_NoModelCall",
        "RuntimeBenchmarkE2ETests.CanonicalBaseline_CreatesReproducibleState_AcrossAllRepresentativeTaskCategories",
        "RuntimeBenchmarkE2ETests.BenchmarkModels_Serialization_RoundTripsAccurately_AndHandlesMissingTelemetryHonesty",
        "RuntimeBenchmarkE2ETests.FileClassification_CategorizesRequiredAndIncidentalCorrectly_ForEveryTask",
        "RuntimeBenchmarkE2ETests.CancellationMatrix_CancelBeforeLaunch_BothAdapters_TerminateCancelledWithoutContainers",
        "RuntimeBenchmarkE2ETests.TimeoutMatrix_ExpiredDeadlineBeforeLaunch_BothAdapters_TerminateTimedOutWithoutContainers",
        "ReviewExecutionLiveTests.LiveLifecycle_ReviewViaAdapter_AdvancesToHumanReview_AndCompletedOnApproval"
    };

    private static readonly Regex LiveShapedTypeName = new(
        "Live|RealHermes|RealOpenHands|SmokeTest|RuntimeBenchmark|DockerLifecycleIntegration", RegexOptions.Compiled);

    [Fact]
    public void NoOptIn_EveryGateReportsASkipReason_AndNeverAPass()
    {
        string? NoEnv(string _) => null;
        var reloaded = false;

        var reason = LiveTestGate.Evaluate(
            new[] { "AI_FACTORY_HERMES_SMOKE_TEST" }, new[] { "OPENROUTER_API_KEY" }, requiresDocker: true,
            NoEnv, dockerAvailable: () => true, loadEnvLive: () => reloaded = true);

        Assert.StartsWith("SKIPPED — LIVE OPT-IN NOT ENABLED", reason);
        Assert.False(reloaded, ".env.live must not be read without an explicit opt-in.");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("")]
    [InlineData("   ")]
    public void OptInVariableSetToANegativeValue_IsNotAnOptIn(string value)
    {
        var reason = LiveTestGate.Evaluate(
            new[] { "AI_FACTORY_LIVE_SMOKE" }, Array.Empty<string>(), false, _ => value);

        Assert.StartsWith("SKIPPED — LIVE OPT-IN NOT ENABLED", reason);
    }

    [Fact]
    public void OptedInWithoutTheCredential_IsEnvironmentBlocked_NotAPass_AndNamesNoValue()
    {
        string? Env(string name) => name == "AI_FACTORY_HERMES_SMOKE_TEST" ? "true" : null;

        var reason = LiveTestGate.Evaluate(
            new[] { "AI_FACTORY_HERMES_SMOKE_TEST" }, new[] { "OPENROUTER_API_KEY" }, false, Env);

        Assert.StartsWith("ENVIRONMENT BLOCKED", reason);
        Assert.Contains("OPENROUTER_API_KEY", reason);
    }

    [Fact]
    public void OptedIn_ButNeedingNothingFromTheEnvironment_NeverReadsEnvLive()
    {
        string? Env(string name) => name == "AI_FACTORY_OPENHANDS_SMOKE_TEST" ? "true" : null;
        var read = false;

        var reason = LiveTestGate.Evaluate(
            new[] { "AI_FACTORY_OPENHANDS_SMOKE_TEST" }, Array.Empty<string>(), requiresDocker: false, Env, loadEnvLive: () => read = true);

        Assert.Null(reason);
        Assert.False(read, ".env.live was read for a test that declares no required environment.");
    }

    [Fact]
    public void OptedInWithCredentialButNoDocker_IsEnvironmentBlocked()
    {
        string? Env(string name) => name is "AI_FACTORY_HERMES_SMOKE_TEST" or "OPENROUTER_API_KEY" ? "set" : null;

        var reason = LiveTestGate.Evaluate(
            new[] { "AI_FACTORY_HERMES_SMOKE_TEST" }, new[] { "OPENROUTER_API_KEY" }, true, Env, dockerAvailable: () => false);

        Assert.Equal("ENVIRONMENT BLOCKED — Docker daemon not reachable", reason);
    }

    [Fact]
    public void OptedInWithEverythingPresent_MayRun()
    {
        string? Env(string name) => name is "AI_FACTORY_LIVE_SMOKE" or "ANTHROPIC_API_KEY" ? "x" : null;

        Assert.Null(LiveTestGate.Evaluate(
            new[] { "AI_FACTORY_LIVE_SMOKE" }, new[] { "ANTHROPIC_API_KEY" }, false, Env));
    }

    [Fact]
    public void AnAlternativeOptInVariable_IsEnough()
    {
        string? Env(string name) => name == "AI_FACTORY_HERMES_SMOKE_TEST" ? "true" : null;

        Assert.Null(LiveTestGate.Evaluate(
            new[] { "AI_FACTORY_OPENHANDS_SMOKE_TEST", "AI_FACTORY_HERMES_SMOKE_TEST" }, Array.Empty<string>(), false, Env));
    }

    [Fact]
    public void EnvLiveLoader_WithoutOptIn_ReadsNothing_AndSetsNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aif-envlive-" + Guid.NewGuid().ToString("N"));
        var probeName = "AIF_PHASE_O_PROBE_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, ".env.live"), probeName + "=synthetic-not-a-secret\n");

            var loaded = LiveEnvironment.TryLoadEnvLive(dir, optedIn: false);

            Assert.Empty(loaded);
            Assert.Null(Environment.GetEnvironmentVariable(probeName));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnvLiveLoader_WithOptIn_LoadsKeysButNeverAnOptInVariable_AndReturnsNamesOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aif-envlive-" + Guid.NewGuid().ToString("N"));
        var probeName = "AIF_PHASE_O_PROBE_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(
                Path.Combine(dir, ".env.live"),
                probeName + "=synthetic-not-a-secret\nAI_FACTORY_LIVE_SMOKE=ClaudeCode\nAI_FACTORY_HERMES_SMOKE_TEST=true\n");

            var loaded = LiveEnvironment.TryLoadEnvLive(dir, optedIn: true);

            Assert.Equal(new[] { probeName }, loaded);
            Assert.Equal("synthetic-not-a-secret", Environment.GetEnvironmentVariable(probeName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(probeName, null);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EveryLiveShapedTest_UsesAGatedAttribute_NeverAPlainFact()
    {
        var violations = new List<string>();

        foreach (var type in typeof(LiveTestGateTests).Assembly.GetTypes())
        {
            if (type == typeof(LiveTestGateTests) || !LiveShapedTypeName.IsMatch(type.Name))
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attributes = method.GetCustomAttributes(inherit: false);
                var isPlainTest = attributes.Any(a => a.GetType() == typeof(FactAttribute) || a.GetType() == typeof(TheoryAttribute));
                var isGated = attributes.Any(a => a is LiveFactAttribute or LiveTheoryAttribute or DockerFactAttribute or WindowsFactAttribute);

                if (isPlainTest && !isGated && !NonLiveMethodsInLiveNamedTypes.Contains($"{type.Name}.{method.Name}"))
                {
                    violations.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "These live-shaped tests use a plain [Fact]/[Theory] and could run, or 'pass' by returning early, without an explicit opt-in: "
            + string.Join(", ", violations));
    }

    [Fact]
    public void EveryOptInVariableUsedByAnAttribute_IsKnownToTheEnvLiveGuard()
    {
        var unknown = typeof(LiveTestGateTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes(inherit: false))
            .SelectMany(a => a switch
            {
                LiveFactAttribute f => f.OptInVariables,
                LiveTheoryAttribute t => t.OptInVariables,
                _ => Array.Empty<string>()
            })
            .Where(v => !LiveTestGate.KnownOptInVariables.Contains(v))
            .Distinct()
            .ToArray();

        Assert.True(unknown.Length == 0, "Not in LiveTestGate.KnownOptInVariables (so .env.live loading would not recognise them): " + string.Join(", ", unknown));
    }
}
