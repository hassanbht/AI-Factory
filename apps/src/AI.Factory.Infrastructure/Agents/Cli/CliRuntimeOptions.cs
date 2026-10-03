namespace AI.Factory.Infrastructure.Agents.Cli;

/// <summary>
/// Phase M — settings shared by the local-CLI execution adapters (Claude Code, Codex). Section "CliRuntimes".
/// </summary>
public sealed class CliRuntimeOptions
{
    public const string SectionName = "CliRuntimes";

    /// <summary>
    /// Root of the disposable per-run runtime state: <c>{StateRoot}/{agentRunId:N}/</c> holds the run's
    /// isolated CLI home (config, sessions, caches the CLI writes) and its process record. Never inside a
    /// project workspace; deleted when the run ends, reaped after a Worker crash.
    /// </summary>
    public string StateRoot { get; set; } = Path.Combine(Path.GetTempPath(), "ai-factory-runtime");

    /// <summary>
    /// Host environment variables a CLI child may inherit (by name). Everything else is dropped: the
    /// child never sees the Worker's own secrets, the developer's provider keys or CLI configuration.
    /// Home/config/temp variables are always replaced by the run's isolated directories, and a name that
    /// looks like a credential (*KEY*, *TOKEN*, *SECRET*, *PASSWORD*) is never inherited.
    /// </summary>
    public string[] InheritedEnvironmentVariables { get; set; } =
    {
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "OS", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "LANG", "LC_ALL", "TZ", "DOTNET_ROOT", "NUGET_PACKAGES",
        "CLAUDE_CODE_GIT_BASH_PATH"
    };

    internal static void ValidateCommon(string section, string executablePath, string secretName, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException($"{section}:ExecutablePath is required.");
        }

        if (string.IsNullOrWhiteSpace(secretName))
        {
            throw new InvalidOperationException($"{section}:CredentialSecretName is required.");
        }

        if (timeoutSeconds <= 0)
        {
            throw new InvalidOperationException($"{section}:TimeoutSeconds must be positive.");
        }
    }
}

/// <summary>Phase M — the Claude Code execution adapter. Section "ClaudeCode".</summary>
public sealed class ClaudeCodeOptions
{
    public const string SectionName = "ClaudeCode";

    /// <summary>The <c>claude</c> executable: a bare name resolved on PATH, or a full path.</summary>
    public string ExecutablePath { get; set; } = "claude";

    /// <summary>Ceiling for one execution; the Factory deadline (AgentRun) wins when it is earlier.</summary>
    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>Factory secret (ISecretProvider) holding the credential for the run. Required.</summary>
    public string CredentialSecretName { get; set; } = "ANTHROPIC_API_KEY";

    /// <summary>
    /// How the credential reaches the CLI: <c>ANTHROPIC_API_KEY</c> (API key; the run uses <c>--bare</c>, whose
    /// auth is strictly that key) or <c>CLAUDE_CODE_OAUTH_TOKEN</c> (a subscription token from
    /// <c>claude setup-token</c>). Either way it is an environment variable of the child only — never argv.
    /// </summary>
    public string CredentialEnvironmentVariable { get; set; } = "ANTHROPIC_API_KEY";

    /// <summary>
    /// Factory model id (AgentRun.Model, as routed) -> Claude Code <c>--model</c> value. A routed model not
    /// listed here fails the run closed: the adapter never substitutes, defaults or auto-selects a model.
    /// </summary>
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tools a non-interactive run may use without asking (<c>--allowedTools</c>, with
    /// <c>--permission-mode dontAsk</c>: anything else is denied, never prompted). File tools stay inside the
    /// working directory; shell access is limited to the build/test and read-only git commands.
    /// </summary>
    public string[] AllowedTools { get; set; } =
    {
        "Read", "Edit", "Write", "Glob", "Grep", "Bash(dotnet *)", "Bash(git status*)", "Bash(git diff*)"
    };

    /// <summary>Optional <c>--max-budget-usd</c> per run (API-key billing only). Null = not passed.</summary>
    public decimal? MaxBudgetUsd { get; set; }

    public static readonly IReadOnlySet<string> SupportedCredentialVariables =
        new HashSet<string>(StringComparer.Ordinal) { "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN" };

    public void Validate()
    {
        if (!SupportedCredentialVariables.Contains(CredentialEnvironmentVariable))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(CredentialEnvironmentVariable)} must be one of {string.Join(", ", SupportedCredentialVariables)}.");
        }

        CliRuntimeOptions.ValidateCommon(SectionName, ExecutablePath, CredentialSecretName, TimeoutSeconds);
    }
}

/// <summary>Phase M — the Codex execution adapter. Section "Codex".</summary>
public sealed class CodexOptions
{
    public const string SectionName = "Codex";

    /// <summary>
    /// The native <c>codex</c> executable. On Windows npm installs only a <c>codex.cmd</c> shim is on PATH; point
    /// this at the vendored binary (…/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe).
    /// </summary>
    public string ExecutablePath { get; set; } = "codex";

    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>Factory secret holding the OpenAI API key. Required.</summary>
    public string CredentialSecretName { get; set; } = "OPENAI_API_KEY";

    /// <summary>The variable <c>codex exec</c> authenticates with (verified 0.160.0: <c>CODEX_API_KEY</c>).</summary>
    public string CredentialEnvironmentVariable { get; set; } = "CODEX_API_KEY";

    /// <summary>Factory model id -> <c>codex exec -m</c> value; unlisted models fail closed.</summary>
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>--sandbox</c> for model-generated commands. <c>workspace-write</c> (writes only inside the working
    /// directory) is the only value accepted: the Factory never grants Codex broader filesystem access.
    /// </summary>
    public string Sandbox { get; set; } = "workspace-write";

    public static readonly IReadOnlySet<string> PermittedSandboxes =
        new HashSet<string>(StringComparer.Ordinal) { "workspace-write", "read-only" };

    public void Validate()
    {
        if (!PermittedSandboxes.Contains(Sandbox))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Sandbox)} '{Sandbox}' is not permitted; use {string.Join(" or ", PermittedSandboxes)}.");
        }

        if (string.IsNullOrWhiteSpace(CredentialEnvironmentVariable))
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(CredentialEnvironmentVariable)} is required.");
        }

        CliRuntimeOptions.ValidateCommon(SectionName, ExecutablePath, CredentialSecretName, TimeoutSeconds);
    }
}
