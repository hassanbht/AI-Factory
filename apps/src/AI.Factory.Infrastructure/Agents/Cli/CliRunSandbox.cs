using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents.Cli;

/// <summary>
/// Phase M — the disposable runtime state of ONE local-CLI execution:
/// <c>{StateRoot}/{agentRunId:N}/home</c> becomes the child's HOME/USERPROFILE/APPDATA/TEMP and CLI config
/// directory, so the developer's or service account's own CLI configuration, sessions and credentials
/// (~/.claude, ~/.codex, …) are never read, and nothing the CLI writes lands in the project workspace.
/// <c>process.json</c> records the child (pid + start time) so a crashed Worker's successor can reap it.
/// Disposing deletes the whole directory.
/// </summary>
public sealed class CliRunSandbox : IDisposable
{
    private const string ProcessRecordFileName = "process.json";

    private static readonly string[] HomeVariables =
    {
        "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR",
        "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "DOTNET_CLI_HOME"
    };

    private static readonly string[] CredentialLikeFragments = { "KEY", "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL" };

    private readonly ILogger _logger;

    private CliRunSandbox(string runDirectory, ILogger logger)
    {
        RunDirectory = runDirectory;
        HomeDirectory = Path.Combine(runDirectory, "home");
        ScratchDirectory = Path.Combine(runDirectory, "scratch");
        _logger = logger;
    }

    public string RunDirectory { get; }

    public string HomeDirectory { get; }

    /// <summary>Working directory for a run with no Factory workspace (a Planner-only AgentRun).</summary>
    public string ScratchDirectory { get; }

    public static string RunDirectoryFor(string stateRoot, Guid agentRunId) =>
        Path.Combine(Path.GetFullPath(stateRoot), agentRunId.ToString("N"));

    public static CliRunSandbox Create(string stateRoot, Guid agentRunId, ILogger logger)
    {
        var sandbox = new CliRunSandbox(RunDirectoryFor(stateRoot, agentRunId), logger);

        // A directory left by an earlier, crashed attempt of the same AgentRun is stale state, not input.
        DeleteDirectory(sandbox.RunDirectory, logger);
        Directory.CreateDirectory(sandbox.HomeDirectory);
        Directory.CreateDirectory(sandbox.ScratchDirectory);
        return sandbox;
    }

    /// <summary>
    /// The child's complete environment: the allow-listed host variables (minus anything credential-like),
    /// every home/temp variable pointed at the isolated home, the CLI's own config-directory variables, and
    /// the one Factory-selected credential. Nothing else from the Worker reaches the child.
    /// </summary>
    public Dictionary<string, string> BuildEnvironment(
        IEnumerable<string> inheritedNames,
        IEnumerable<string> configDirectoryVariables,
        string credentialVariable,
        string credentialValue,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in inheritedNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value) && !LooksLikeCredential(name))
            {
                environment[name] = value;
            }
        }

        foreach (var name in HomeVariables.Concat(configDirectoryVariables))
        {
            environment[name] = HomeDirectory;
        }

        // Package caches are not credentials; without this every run would re-download all of NuGet.
        if (!environment.ContainsKey("NUGET_PACKAGES"))
        {
            environment["NUGET_PACKAGES"] = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }

        foreach (var (name, value) in extra ?? new Dictionary<string, string>())
        {
            environment[name] = value;
        }

        environment[credentialVariable] = credentialValue;
        return environment;
    }

    public static bool LooksLikeCredential(string variableName) =>
        CredentialLikeFragments.Any(f => variableName.Contains(f, StringComparison.OrdinalIgnoreCase));

    public void RecordProcess(Process process)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(RunDirectory, ProcessRecordFileName),
                JsonSerializer.Serialize(new ProcessRecord(process.Id, process.StartTime.ToUniversalTime())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "CLI process record could not be written to {RunDirectory}.", RunDirectory);
        }
    }

    public void Dispose() => DeleteDirectory(RunDirectory, _logger);

    /// <summary>
    /// Kills the process recorded in <paramref name="runDirectory"/> (and its tree) if it is still the same
    /// process — the start time guards against a recycled pid. True when something was killed.
    /// </summary>
    public static bool KillRecordedProcess(string runDirectory, ILogger logger)
    {
        var path = Path.Combine(runDirectory, ProcessRecordFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var record = JsonSerializer.Deserialize<ProcessRecord>(File.ReadAllText(path));
            if (record is null)
            {
                return false;
            }

            using var process = Process.GetProcessById(record.Pid);
            if (Math.Abs((process.StartTime.ToUniversalTime() - record.StartTimeUtc).TotalSeconds) > 2)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
            return true;
        }
        catch (ArgumentException)
        {
            return false; // No process with that id any more: it already exited.
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException or AggregateException or NotSupportedException)
        {
            logger.LogWarning(ex, "Recorded CLI process in {RunDirectory} could not be inspected or killed.", runDirectory);
            return false;
        }
    }

    public static void DeleteDirectory(string directory, ILogger logger)
    {
        for (var attempt = 1; attempt <= 3 && Directory.Exists(directory); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    logger.LogWarning(ex, "CLI runtime state {Directory} could not be deleted; the reaper will retry.", directory);
                }
                else
                {
                    Thread.Sleep(200 * attempt);
                }
            }
        }
    }

    private sealed record ProcessRecord(int Pid, DateTime StartTimeUtc);
}
