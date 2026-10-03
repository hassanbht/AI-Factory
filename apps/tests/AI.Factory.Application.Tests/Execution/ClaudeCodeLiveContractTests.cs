using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Tests.Gateway;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using AI.Factory.TestSupport;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase N — the REAL installed Claude Code CLI driven by the REAL <see cref="ClaudeCodeExecutionAdapter"/> and
/// <see cref="SystemProcessRunner"/>, with the CLI's API endpoint (<c>ANTHROPIC_BASE_URL</c>) pointed at a local
/// scripted server. Offline, free, deterministic. Opt-in:
/// <c>AI_FACTORY_LIVE_CLAUDE_CONTRACT=1</c> and <c>ClaudeCode__ExecutablePath=&lt;path to claude.exe&gt;</c>.
///
/// <b>What this proves:</b> the installed CLI accepts the adapter's exact invocation; the Factory credential — and
/// only it — reaches the provider endpoint, whatever ambient credentials/config exist; the first 401 stops the run in
/// seconds; a hung provider is cut by the Factory deadline and by cancellation with the real process tree gone; and
/// the CLI's real <c>result</c> event schema is read correctly.
///
/// <b>What it does NOT prove:</b> anything about a model. The "model" here is a script. A pass is NOT live
/// model-backed certification — that needs a real credential and is reported separately.
/// </summary>
[Collection(TestCollections.RealProcesses)]
public sealed class ClaudeCodeLiveContractTests : IDisposable
{
    private const string FactoryKey = "sk-ant-api03-PHASEN-FACTORY-SYNTHETIC-aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AmbientKey = "sk-ant-api03-PHASEN-AMBIENT-SYNTHETIC-bbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string AmbientOauth = "sk-ant-oat01-PHASEN-AMBIENT-SYNTHETIC-cccccccccccccccccccccc";
    private const string FactoryOauth = "sk-ant-oat01-PHASEN-FACTORY-SYNTHETIC-dddddddddddddddddddddd";
    private const string RoutedModel = "factory/model-a";

    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aif-claude-contract-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _stateRoot;

    public ClaudeCodeLiveContractTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        _stateRoot = Path.Combine(_root, "state");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output.WriteLine($"cleanup: {ex.Message}");
        }
    }

    // Phase O — the opt-in and the path are enforced by the [LiveFact]/[LiveTheory] attributes below (a missing opt-in is SKIPPED,
    // a missing path ENVIRONMENT BLOCKED), so a test body can never silently "pass" without running.
    private static string Executable => Environment.GetEnvironmentVariable("ClaudeCode__ExecutablePath")!;

    // ---------------------------------------------------------------- scripted provider endpoint

    private sealed record Seen(string? ApiKeyHeader, string? Authorization, string? Model, string[] Tools, bool HasToolResult);

    private enum Mode
    {
        Unauthorized,
        Hang,
        ScriptedFileTask
    }

    private sealed class ScriptedProvider : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly Mode _mode;
        private readonly string _filePath;
        private readonly string _fileContent;

        public ScriptedProvider(Mode mode, string filePath = "", string fileContent = "")
        {
            _mode = mode;
            _filePath = filePath;
            _fileContent = fileContent;
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public List<Seen> Requests { get; } = new();

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                var isMessages = context.Request.Url!.AbsolutePath.EndsWith("/v1/messages", StringComparison.Ordinal);
                if (!isMessages)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                string? model = null;
                var tools = Array.Empty<string>();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
                    tools = doc.RootElement.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array
                        ? t.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty).ToArray()
                        : Array.Empty<string>();
                }
                catch (JsonException)
                {
                    // Not JSON: recorded as an unknown request below.
                }

                var hasToolResult = body.Contains("\"tool_result\"", StringComparison.Ordinal);
                lock (Requests)
                {
                    Requests.Add(new Seen(
                        context.Request.Headers["x-api-key"], context.Request.Headers["Authorization"], model, tools, hasToolResult));
                }

                switch (_mode)
                {
                    case Mode.Unauthorized:
                        context.Response.StatusCode = 401;
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
                            "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}"));
                        context.Response.Close();
                        break;

                    case Mode.Hang:
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        break;

                    case Mode.ScriptedFileTask:
                        await WriteScriptedStreamAsync(context, model ?? "claude-haiku-4-5", tools, hasToolResult);
                        break;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException)
            {
                // The client (the CLI) went away — exactly what timeout/cancellation does.
            }
        }

        private async Task WriteScriptedStreamAsync(HttpListenerContext context, string model, string[] tools, bool afterToolResult)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;

            async Task Event(string type, object data)
            {
                var text = $"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n";
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text));
                await context.Response.OutputStream.FlushAsync();
            }

            await Event("message_start", new
            {
                type = "message_start",
                message = new
                {
                    id = "msg_scripted_" + Guid.NewGuid().ToString("N")[..8],
                    type = "message",
                    role = "assistant",
                    model,
                    content = Array.Empty<object>(),
                    stop_reason = (string?)null,
                    stop_sequence = (string?)null,
                    usage = new { input_tokens = 120, output_tokens = 1 }
                }
            });

            if (afterToolResult)
            {
                await Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
                await Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Created the file as instructed." } });
                await Event("content_block_stop", new { type = "content_block_stop", index = 0 });
                await Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 9 } });
            }
            else
            {
                // --bare exposes Bash/Edit/Read; the subscription path the full set. Create the file with whichever file tool is offered.
                var useWrite = tools.Contains("Write");
                var toolName = useWrite ? "Write" : "Edit";
                object input = useWrite
                    ? new { file_path = _filePath, content = _fileContent }
                    : new { file_path = _filePath, old_string = "", new_string = _fileContent };
                await Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = "toolu_scripted_1", name = toolName, input = new { } } });
                await Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(input) } });
                await Event("content_block_stop", new { type = "content_block_stop", index = 0 });
                await Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use", stop_sequence = (string?)null }, usage = new { output_tokens = 30 } });
            }

            await Event("message_stop", new { type = "message_stop" });
            context.Response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Abort();
            try
            {
                await _loop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Shutting down.
            }
        }
    }

    // ---------------------------------------------------------------- adapter under test

    private ClaudeCodeExecutionAdapter CreateAdapter(string credentialVariable, string credentialValue, string baseUrl)
    {
        var cli = new CliRuntimeOptions
        {
            StateRoot = _stateRoot,
            // ANTHROPIC_BASE_URL is the test seam: not a credential, and not inherited by default.
            InheritedEnvironmentVariables = new CliRuntimeOptions().InheritedEnvironmentVariables.Append("ANTHROPIC_BASE_URL").ToArray()
        };
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", baseUrl);

        var options = new ClaudeCodeOptions
        {
            ExecutablePath = Executable,
            CredentialSecretName = credentialVariable,
            CredentialEnvironmentVariable = credentialVariable,
            Models = new() { [RoutedModel] = "claude-haiku-4-5" }
        };
        var runner = new SystemProcessRunner(new CommandPolicyOptions { AllowedCommands = new[] { "claude" } });
        var secrets = new FakeSecretProvider(new Dictionary<string, string?> { [credentialVariable] = credentialValue });
        return new ClaudeCodeExecutionAdapter(options, cli, runner, secrets, NullLogger<ClaudeCodeExecutionAdapter>.Instance);
    }

    private AgentExecutionSpec Spec(DateTimeOffset? deadline = null) => new(
        AgentRunId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(), ProjectId: Guid.NewGuid(), WorkItemId: Guid.NewGuid(),
        Attempt: 1, Iteration: 0,
        Agent: new ExecutionAgent(Guid.NewGuid(), "coder", "Implementation", "You are a coding agent."),
        Instructions: "Create FACTORY_CLAUDE_SMOKE.txt containing exactly AI_FACTORY_CLAUDE_SMOKE_OK.",
        Workspace: new ExecutionWorkspace(_workspace),
        Model: new ModelRoute(RoutedModel, "openrouter"),
        Timeout: TimeSpan.FromMinutes(5), Deadline: deadline);

    private static void Ambient(bool set)
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", set ? AmbientKey : null);
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", set ? AmbientOauth : null);
    }

    private static void Restore(string? previousBaseUrl)
    {
        Ambient(set: false);
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", previousBaseUrl);
    }

    // ---------------------------------------------------------------- tests

    [LiveTheory("AI_FACTORY_LIVE_CLAUDE_CONTRACT", RequiredEnvironment = new[] { "ClaudeCode__ExecutablePath" })]
    [InlineData("ANTHROPIC_API_KEY", FactoryKey)]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", FactoryOauth)]
    public async Task FactoryCredential_IsTheOnlyOneTheProviderEverSees_AndTheFirst401StopsTheRunInSeconds(
        string credentialVariable, string factoryCredential)
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        await using var provider = new ScriptedProvider(Mode.Unauthorized);
        Ambient(set: true); // ambient credentials A that must never win
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var outcome = await CreateAdapter(credentialVariable, factoryCredential, provider.Url)
                .ExecuteAsync(Spec(deadline: DateTimeOffset.UtcNow.AddMinutes(3)));
            stopwatch.Stop();

            _output.WriteLine($"mode={credentialVariable} status={outcome.Status} reason={outcome.ExitReason} "
                + $"failure={outcome.GatewayFailure} elapsed={stopwatch.Elapsed.TotalSeconds:F1}s requests={provider.Requests.Count}");

            Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
            Assert.Equal(FailureCategory.Provider, outcome.FailureCategory);
            Assert.Equal("Authentication", outcome.GatewayFailure);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"took {stopwatch.Elapsed}; the CLI's own retry loop is minutes");

            Assert.NotEmpty(provider.Requests);
            foreach (var seen in provider.Requests)
            {
                var presented = credentialVariable == "ANTHROPIC_API_KEY" ? seen.ApiKeyHeader : seen.Authorization;
                Assert.Contains(factoryCredential, presented ?? string.Empty);
                Assert.DoesNotContain(AmbientKey, seen.ApiKeyHeader + seen.Authorization);
                Assert.DoesNotContain(AmbientOauth, seen.ApiKeyHeader + seen.Authorization);
            }

            Assert.DoesNotContain(factoryCredential, outcome.ErrorMessage ?? string.Empty);
            Assert.True(!Directory.Exists(_stateRoot) || Directory.GetFileSystemEntries(_stateRoot).Length == 0);
            Assert.Empty(Directory.GetFileSystemEntries(_workspace));
        }
        finally
        {
            Restore(previous);
        }
    }

    [LiveFact("AI_FACTORY_LIVE_CLAUDE_CONTRACT", RequiredEnvironment = new[] { "ClaudeCode__ExecutablePath" })]
    public async Task ScriptedEndpoint_RealCli_CreatesTheFile_AndTheRealResultEventIsReadCorrectly()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        var target = Path.Combine(_workspace, "FACTORY_CLAUDE_SMOKE.txt");
        await using var provider = new ScriptedProvider(Mode.ScriptedFileTask, target, "AI_FACTORY_CLAUDE_SMOKE_OK");
        Ambient(set: true);
        try
        {
            var spec = Spec(deadline: DateTimeOffset.UtcNow.AddMinutes(3));
            var outcome = await CreateAdapter("ANTHROPIC_API_KEY", FactoryKey, provider.Url).ExecuteAsync(spec);

            _output.WriteLine($"status={outcome.Status} reason={outcome.ExitReason} error={outcome.ErrorMessage} "
                + $"output='{outcome.Output}' usage={outcome.Usage?.PromptTokens}/{outcome.Usage?.CompletionTokens} "
                + $"session={outcome.Report?.SessionId} cost={outcome.Report?.CostUsd} turns={outcome.Report?.Turns} "
                + $"reportedModel={outcome.Report?.ReportedModel} requests={provider.Requests.Count}");

            Assert.True(outcome.Status == ExecutionOutcomeStatus.Succeeded, $"{outcome.Status}: {outcome.ErrorMessage}");
            Assert.True(File.Exists(target), "the real CLI executed the scripted tool call inside the workspace");
            Assert.Equal("AI_FACTORY_CLAUDE_SMOKE_OK", File.ReadAllText(target).Trim());
            Assert.Equal(new[] { "FACTORY_CLAUDE_SMOKE.txt" }, Directory.GetFileSystemEntries(_workspace).Select(Path.GetFileName).ToArray());
            Assert.Equal("Created the file as instructed.", outcome.Output);
            Assert.NotNull(outcome.Usage);
            Assert.Equal(spec.AgentRunId.ToString("D"), outcome.Report?.SessionId); // --session-id is honoured
            Assert.Equal("claude-haiku-4-5", outcome.Report?.ReportedModel);
            Assert.All(provider.Requests, r => Assert.Contains(FactoryKey, r.ApiKeyHeader ?? string.Empty));
            Assert.True(!Directory.Exists(_stateRoot) || Directory.GetFileSystemEntries(_stateRoot).Length == 0);
        }
        finally
        {
            Restore(previous);
        }
    }

    [LiveFact("AI_FACTORY_LIVE_CLAUDE_CONTRACT", RequiredEnvironment = new[] { "ClaudeCode__ExecutablePath" })]
    public async Task HungProvider_FactoryDeadline_TerminatesTheRealProcessTree_AsTimedOut()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        await using var provider = new ScriptedProvider(Mode.Hang);
        try
        {
            var adapter = CreateAdapter("ANTHROPIC_API_KEY", FactoryKey, provider.Url);
            var run = adapter.ExecuteAsync(Spec(deadline: DateTimeOffset.UtcNow.AddSeconds(25)));

            var tree = await WatchProcessTree();
            var outcome = await run;

            _output.WriteLine($"status={outcome.Status} reason={outcome.ExitReason} watched={tree.Length} pids");
            Assert.Equal(ExecutionOutcomeStatus.TimedOut, outcome.Status);
            Assert.NotEmpty(tree);
            await AssertAllDead(tree);
            Assert.True(!Directory.Exists(_stateRoot) || Directory.GetFileSystemEntries(_stateRoot).Length == 0);
        }
        finally
        {
            Restore(previous);
        }
    }

    [LiveFact("AI_FACTORY_LIVE_CLAUDE_CONTRACT", RequiredEnvironment = new[] { "ClaudeCode__ExecutablePath" })]
    public async Task HungProvider_Cancellation_TerminatesTheRealProcessTree_AsCancelled()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        await using var provider = new ScriptedProvider(Mode.Hang);
        using var cts = new CancellationTokenSource();
        try
        {
            var adapter = CreateAdapter("ANTHROPIC_API_KEY", FactoryKey, provider.Url);
            var run = adapter.ExecuteAsync(Spec(), cts.Token);

            var tree = await WatchProcessTree();
            Assert.NotEmpty(tree);
            // Wait until the CLI really talks to the provider, i.e. cancel in the middle of active execution.
            var stopwatch = Stopwatch.StartNew();
            while (provider.Requests.Count == 0 && stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(100);
            }

            cts.Cancel();
            var outcome = await run;

            _output.WriteLine($"status={outcome.Status} requestsBeforeCancel={provider.Requests.Count} watched={tree.Length} pids");
            Assert.Equal(ExecutionOutcomeStatus.Cancelled, outcome.Status);
            Assert.NotEmpty(provider.Requests);
            await AssertAllDead(tree);
            Assert.True(!Directory.Exists(_stateRoot) || Directory.GetFileSystemEntries(_stateRoot).Length == 0);
        }
        finally
        {
            Restore(previous);
        }
    }

    // ---------------------------------------------------------------- process-tree observation (Windows)

    /// <summary>The recorded root process of the run plus its descendants, sampled while the CLI is running.</summary>
    private async Task<int[]> WatchProcessTree()
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (Directory.Exists(_stateRoot))
            {
                var record = Directory.EnumerateFiles(_stateRoot, "process.json", SearchOption.AllDirectories).FirstOrDefault();
                if (record is not null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(record));
                    var root = doc.RootElement.GetProperty("Pid").GetInt32();
                    await Task.Delay(3000); // let the CLI start its own helpers
                    return new[] { root }.Concat(Descendants(root)).Distinct().ToArray();
                }
            }

            await Task.Delay(100);
        }

        return Array.Empty<int>();
    }

    private static IEnumerable<int> Descendants(int rootPid)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("Get-CimInstance Win32_Process | ForEach-Object { \"$($_.ProcessId),$($_.ParentProcessId)\" }");
        using var ps = Process.Start(info)!;
        var lines = ps.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        ps.WaitForExit();

        var parentOf = lines.Select(l => l.Trim().Split(',')).Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            .ToDictionary(p => int.Parse(p[0]), p => int.Parse(p[1]));
        var found = new HashSet<int>();
        var frontier = new Queue<int>(new[] { rootPid });
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var (pid, parent) in parentOf)
            {
                if (parent == current && found.Add(pid))
                {
                    frontier.Enqueue(pid);
                }
            }
        }

        return found;
    }

    private static async Task AssertAllDead(int[] pids)
    {
        var stopwatch = Stopwatch.StartNew();
        int[] alive;
        do
        {
            alive = pids.Where(IsAlive).ToArray();
            if (alive.Length == 0)
            {
                return;
            }

            await Task.Delay(100);
        }
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10));

        Assert.Empty(alive);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
