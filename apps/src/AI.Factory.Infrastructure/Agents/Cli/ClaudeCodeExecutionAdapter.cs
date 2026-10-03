using System.Globalization;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents.Cli;

/// <summary>
/// Phase M — Claude Code as an execution runtime behind the provider-neutral dispatcher.
///
/// Invocation contract, verified against the installed CLI (2.1.121 in Phase M; every flag and the init/api_retry
/// event shapes re-verified against 2.1.284 in Phase N, see docs/phase-n-report.md) and live probes:
/// <c>claude -p --output-format stream-json --verbose --model M --session-id {AgentRunId}
/// --no-session-persistence --permission-mode dontAsk --setting-sources "" --strict-mcp-config
/// --disable-slash-commands [--bare] --allowedTools …</c>, prompt on stdin, cwd = the Factory workspace.
/// <list type="bullet">
/// <item>Isolation: CLAUDE_CONFIG_DIR/HOME point at the run's own empty home, so the operator's ~/.claude login,
/// settings, hooks, plugins, MCP servers and CLAUDE.md memory are never loaded (verified: an isolated run with
/// no credential answers "Not logged in" on a logged-in machine). No user/project/local settings are read.</item>
/// <item>Permissions: <c>dontAsk</c> + an explicit allow-list — nothing outside it is permitted and nothing ever
/// prompts, so a run cannot block the Worker on an interactive question.</item>
/// <item>Credential: the Factory secret, as ANTHROPIC_API_KEY (with <c>--bare</c>, whose auth is strictly that
/// key) or CLAUDE_CODE_OAUTH_TOKEN, in the child environment only.</item>
/// <item>Session: the session id is the AgentRun id (deterministic, non-secret); nothing is persisted for
/// resume — a retry is a new AgentRun, owned by the Factory, not a CLI conversation.</item>
/// <item>Result: only the CLI's final <c>result</c> event can make a run succeed (<c>is_error</c> false,
/// subtype <c>success</c>, exit 0). Cost, turns, tokens and session id are taken from it when present.
/// Claude Code retries a 401 ten times with exponential backoff (observed live); the first
/// <c>api_retry</c> with 401/403 aborts the run as an authentication failure instead.</item>
/// </list>
/// </summary>
public sealed class ClaudeCodeExecutionAdapter : CliExecutionAdapter
{
    public const string AdapterKey = "ClaudeCode";

    private readonly ClaudeCodeOptions _options;

    public ClaudeCodeExecutionAdapter(
        ClaudeCodeOptions options,
        CliRuntimeOptions cliOptions,
        IProcessRunner processRunner,
        ISecretProvider secretProvider,
        ILogger<ClaudeCodeExecutionAdapter> logger)
        : base(processRunner, secretProvider, cliOptions, logger)
    {
        _options = options;
    }

    public override string Key => AdapterKey;

    protected override CliAdapterSettings Settings => new(
        _options.ExecutablePath,
        _options.TimeoutSeconds,
        _options.CredentialSecretName,
        _options.CredentialEnvironmentVariable,
        _options.Models,
        new[] { "CLAUDE_CONFIG_DIR" },
        ProviderName: "anthropic");

    protected override IReadOnlyDictionary<string, string> ExtraEnvironment { get; } = new Dictionary<string, string>
    {
        ["DISABLE_AUTOUPDATER"] = "1",
        ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1"
    };

    protected override IReadOnlyList<string> BuildArguments(AgentExecutionSpec spec, string cliModel, string workingDirectory)
    {
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--model", cliModel,
            "--session-id", spec.AgentRunId.ToString("D"),
            "--no-session-persistence",
            "--permission-mode", "dontAsk",
            "--setting-sources", string.Empty,
            "--strict-mcp-config",
            "--disable-slash-commands"
        };

        if (_options.CredentialEnvironmentVariable == "ANTHROPIC_API_KEY")
        {
            arguments.Add("--bare");
        }

        if (_options.MaxBudgetUsd is { } budget)
        {
            arguments.Add("--max-budget-usd");
            arguments.Add(budget.ToString(CultureInfo.InvariantCulture));
        }

        // Last: --allowedTools is variadic.
        arguments.Add("--allowedTools");
        arguments.AddRange(_options.AllowedTools);
        return arguments;
    }

    protected override CliEventStream CreateEventStream() => new ClaudeStreamJson();

    /// <summary>Reads Claude Code's <c>--output-format stream-json</c> events (one JSON object per line).</summary>
    public sealed class ClaudeStreamJson : CliEventStream
    {
        private JsonElement? _result;
        private string? _initModel;
        private string? _initSessionId;

        public override void Observe(string line)
        {
            if (!CliJson.TryParseObject(line, out var root))
            {
                return; // Not an event (or malformed): never interpreted as success.
            }

            switch (CliJson.Text(root, "type"))
            {
                case "system" when CliJson.Text(root, "subtype") == "init":
                    _initModel = CliJson.Text(root, "model");
                    _initSessionId = CliJson.Text(root, "session_id");
                    break;

                case "system" when CliJson.Text(root, "subtype") == "api_retry"
                    && CliJson.Int(root, "error_status") is 401 or 403:
                    AbortReason = $"Claude Code authentication failed (HTTP {CliJson.Int(root, "error_status")}, "
                        + $"{CliJson.Text(root, "error") ?? "authentication_failed"}); the run was stopped instead of retrying.";
                    AbortGatewayFailure = AuthenticationFailure;
                    break;

                case "result":
                    _result = root;
                    break;
            }
        }

        public override CliInterpretation Interpret(int exitCode)
        {
            if (_result is not { } result)
            {
                return new CliInterpretation(false, null,
                    $"Claude Code produced no result event (exit code {exitCode}); success cannot be proven.",
                    FailureCategory.Agent, SessionId: _initSessionId, ReportedModel: _initModel);
            }

            var text = CliJson.Text(result, "result");
            var isError = !result.TryGetProperty("is_error", out var isErrorElement) || isErrorElement.ValueKind != JsonValueKind.False;
            var subtype = CliJson.Text(result, "subtype");
            var authFailure = CliJson.Int(result, "api_error_status") is 401 or 403
                || (text is not null && (text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)));

            // "subtype":"success" alone proves nothing: an unauthenticated run reports subtype success
            // with is_error true (observed live, 2.1.121).
            var succeeded = !isError && subtype == "success";
            var error = succeeded
                ? null
                : authFailure
                    ? $"Claude Code authentication failed: {text}"
                    : $"Claude Code reported {(isError ? "an error" : "an incomplete run")} (subtype '{subtype}', "
                        + $"terminal_reason '{CliJson.Text(result, "terminal_reason")}'): {text}";

            return new CliInterpretation(
                succeeded,
                text,
                error,
                succeeded ? null : authFailure ? FailureCategory.Provider : FailureCategory.Agent,
                GatewayFailure: authFailure ? AuthenticationFailure : null,
                Usage: Usage(result),
                SessionId: CliJson.Text(result, "session_id") ?? _initSessionId,
                CostUsd: CliJson.Decimal(result, "total_cost_usd"),
                CostSource: "claude:total_cost_usd",
                Turns: CliJson.Int(result, "num_turns"),
                ReportedModel: _initModel);
        }

        /// <summary>Exactly what the result's usage object states; an absent object means "not exposed".</summary>
        private static ExecutionUsage? Usage(JsonElement result)
        {
            if (!result.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var input = CliJson.Int(usage, "input_tokens");
            var output = CliJson.Int(usage, "output_tokens");
            return input is null && output is null
                ? null
                : new ExecutionUsage(input, output, input is { } i && output is { } o ? i + o : null);
        }
    }
}

/// <summary>Small, strict JSON helpers shared by the CLI event readers.</summary>
public static class CliJson
{
    public static bool TryParseObject(string line, out JsonElement root)
    {
        root = default;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            root = document.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;

    public static decimal? Decimal(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var v) && v >= 0
            ? v
            : null;
}
