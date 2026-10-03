using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents.Cli;

/// <summary>
/// Phase M — OpenAI Codex CLI as an execution runtime behind the provider-neutral dispatcher.
///
/// Invocation contract, verified against the installed CLI (codex-cli 0.160.0, <c>codex exec --help</c>) and
/// live probes: <c>codex exec --json --ephemeral --skip-git-repo-check --ignore-user-config --ignore-rules
/// --color never -c approval_policy="never" -s workspace-write -m M -C {workspace} -</c>, prompt on stdin.
/// <list type="bullet">
/// <item>Isolation: CODEX_HOME/HOME point at the run's own empty home; <c>--ignore-user-config</c> and
/// <c>--ignore-rules</c> skip config.toml and execpolicy rules; <c>--ephemeral</c> persists no session files.
/// ~/.codex/auth.json is never consulted.</item>
/// <item>Policy: the Factory decides — <c>approval_policy=never</c> (no prompt can block the Worker) and the
/// <c>workspace-write</c> sandbox (writes confined to the working directory). Broader sandboxes are refused.</item>
/// <item>Credential: the Factory secret as CODEX_API_KEY in the child environment only (verified live: OpenAI
/// rejected exactly the injected probe key, "Incorrect API key provided: sk-inval…"). Codex strips *KEY*
/// variables from the shells it spawns, so model-generated commands do not see it.</item>
/// <item>Result: success needs a <c>turn.completed</c> event, no <c>turn.failed</c>, and exit 0. Usage comes from
/// <c>turn.completed.usage</c>; the session id from <c>thread.started</c>; Codex reports no cost. The first
/// HTTP 401 aborts the run instead of letting Codex retry it ten times.</item>
/// </list>
/// </summary>
public sealed partial class CodexExecutionAdapter : CliExecutionAdapter
{
    public const string AdapterKey = "Codex";

    private readonly CodexOptions _options;

    public CodexExecutionAdapter(
        CodexOptions options,
        CliRuntimeOptions cliOptions,
        IProcessRunner processRunner,
        ISecretProvider secretProvider,
        ILogger<CodexExecutionAdapter> logger)
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
        new[] { "CODEX_HOME" },
        ProviderName: "openai");

    protected override IReadOnlyList<string> BuildArguments(AgentExecutionSpec spec, string cliModel, string workingDirectory) => new[]
    {
        "exec",
        "--json",
        "--ephemeral",
        "--skip-git-repo-check",
        "--ignore-user-config",
        "--ignore-rules",
        "--color", "never",
        "-c", "approval_policy=\"never\"",
        "-s", _options.Sandbox,
        "-m", cliModel,
        "-C", workingDirectory,
        "-"
    };

    protected override CliEventStream CreateEventStream() => new CodexJsonl();

    /// <summary>Reads <c>codex exec --json</c> JSONL thread events.</summary>
    public sealed partial class CodexJsonl : CliEventStream
    {
        private string? _threadId;
        private string? _lastAgentMessage;
        private string? _turnFailure;
        private string? _lastError;
        private ExecutionUsage? _usage;
        private bool _turnCompleted;

        public override void Observe(string line)
        {
            if (!CliJson.TryParseObject(line, out var root))
            {
                return;
            }

            switch (CliJson.Text(root, "type"))
            {
                case "thread.started":
                    _threadId = CliJson.Text(root, "thread_id");
                    break;

                case "item.completed" when root.TryGetProperty("item", out var item):
                    if (CliJson.Text(item, "type") == "agent_message")
                    {
                        _lastAgentMessage = CliJson.Text(item, "text");
                    }
                    else if (CliJson.Text(item, "type") == "error")
                    {
                        ObserveError(CliJson.Text(item, "message"));
                    }

                    break;

                case "error":
                    ObserveError(CliJson.Text(root, "message"));
                    break;

                case "turn.failed":
                    _turnFailure = root.TryGetProperty("error", out var error) ? CliJson.Text(error, "message") : null;
                    _turnFailure ??= "turn failed";
                    break;

                case "turn.completed":
                    _turnCompleted = true;
                    _usage = Usage(root);
                    break;
            }
        }

        public override CliInterpretation Interpret(int exitCode)
        {
            var failure = _turnFailure ?? (_turnCompleted ? null : _lastError);
            var authFailure = IsAuthenticationFailure(failure);
            var succeeded = _turnCompleted && _turnFailure is null;

            var error = succeeded
                ? null
                : failure is not null
                    ? $"Codex {(authFailure ? "authentication failed" : "reported a failed turn")}: {failure}"
                    : $"Codex produced no turn.completed event (exit code {exitCode}); success cannot be proven.";

            return new CliInterpretation(
                succeeded,
                _lastAgentMessage,
                error,
                succeeded ? null : authFailure ? FailureCategory.Provider : FailureCategory.Agent,
                GatewayFailure: authFailure ? AuthenticationFailure : null,
                Usage: _usage,
                SessionId: _threadId,
                UpstreamRequestId: RequestId(failure));
        }

        private void ObserveError(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            _lastError = message;

            if (IsAuthenticationFailure(message) && AbortReason is null)
            {
                AbortReason = $"Codex authentication failed; the run was stopped instead of retrying: {message}";
                AbortGatewayFailure = AuthenticationFailure;
            }
        }

        private static bool IsAuthenticationFailure(string? message) =>
            message is not null && (message.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase)
                || message.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase));

        /// <summary>Only an id Codex printed explicitly ("request id: req_…") — never synthesized.</summary>
        private static string? RequestId(string? message) =>
            message is not null && RequestIdPattern().Match(message) is { Success: true } match ? match.Groups[1].Value : null;

        private static ExecutionUsage? Usage(JsonElement turnCompleted)
        {
            if (!turnCompleted.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var input = CliJson.Int(usage, "input_tokens");
            var output = CliJson.Int(usage, "output_tokens");
            return input is null && output is null
                ? null
                : new ExecutionUsage(input, output, input is { } i && output is { } o ? i + o : null);
        }

        [GeneratedRegex(@"request id: (req_[A-Za-z0-9]+)")]
        private static partial Regex RequestIdPattern();
    }
}
