using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using AI.Factory.TestSupport;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase M — LIVE (opt-in: AI_FACTORY_LIVE_HERMES_CREDENTIALS=1; needs Docker and the Hermes image) proof of
/// credential precedence with two different credentials, at zero cost: the real Hermes container talks to a
/// local mock "OpenRouter" (OPENROUTER_BASE_URL) that records which bearer key arrives and answers 401.
/// <list type="number">
/// <item>Reproduction of the benchmark defect: a Hermes home holding an ambient <c>.env</c> key, mounted the way
/// the Factory used to mount ~/.hermes, wins over the key injected with <c>-e</c>.</item>
/// <item>The Phase M executor: the same ambient home exists on the host, the Factory key is the only one that
/// ever reaches the provider.</item>
/// </list>
/// </summary>
[Collection(TestCollections.DockerAndBuilds)]
public sealed class HermesCredentialPrecedenceLiveTests : IDisposable
{
    private const string FactoryKey = "sk-or-v1-factory-selected-credential-0001";
    private const string AmbientKey = "sk-or-v1-ambient-developer-credential-0002";
    private const string Image = "nousresearch/hermes-agent:0.20.6";

    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aif-hermes-cred-" + Guid.NewGuid().ToString("N"));

    public HermesCredentialPrecedenceLiveTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
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

    /// <summary>Minimal HTTP endpoint: records every Authorization header, answers 401.</summary>
    private sealed class RecordingProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public RecordingProvider()
        {
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Authorizations { get; } = new();

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[64 * 1024];
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        var request = Encoding.UTF8.GetString(buffer, 0, read);
                        var auth = request.Split("\r\n").FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
                        lock (Authorizations)
                        {
                            Authorizations.Add(auth?["Authorization:".Length..].Trim() ?? "(none)");
                        }

                        const string body = "{\"error\":{\"message\":\"mock provider: unauthorized\",\"code\":401}}";
                        var response = $"HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), _stop.Token);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException)
                    {
                        // A client that hung up mid-request: nothing to record.
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
        }
    }

    private string AmbientHermesHome()
    {
        var home = Directory.CreateDirectory(Path.Combine(_root, "ambient-hermes-home")).FullName;
        File.WriteAllText(Path.Combine(home, ".env"), $"OPENROUTER_API_KEY={AmbientKey}\n");
        return home;
    }

    private static string BaseUrl(RecordingProvider provider) => $"http://host.docker.internal:{provider.Port}/api/v1";

    [LiveFact("AI_FACTORY_LIVE_HERMES_CREDENTIALS", RequiresDocker = true)]
    public async Task OldMountStyle_AmbientHermesEnvWins_ReproducingTheBenchmarkDefect()
    {
        await using var provider = new RecordingProvider();
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "ws-old")).FullName;
        var info = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[]
                 {
                     "run", "--rm", "--network", "bridge",
                     "-v", $"{AmbientHermesHome()}:/opt/data", // what HermesOptions.DataVolume (default ~/.hermes) used to do
                     "-v", $"{workspace}:/workspace",
                     "-e", "OPENROUTER_API_KEY", "-e", $"OPENROUTER_BASE_URL={BaseUrl(provider)}",
                     Image, "-z", "Say OK.", "-m", "mock/model", "--provider", "openrouter"
                 })
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["OPENROUTER_API_KEY"] = FactoryKey;
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token);
        _output.WriteLine($"old-style exit {process.ExitCode}; stdout tail: {Tail(await stdout)}; stderr tail: {Tail(await stderr)}");

        _output.WriteLine($"old-style keys seen by provider: {string.Join(", ", provider.Authorizations.Distinct().Select(Mask))}");
        Assert.NotEmpty(provider.Authorizations);
        Assert.Contains(provider.Authorizations, a => a.Contains(AmbientKey, StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Authorizations, a => a.Contains(FactoryKey, StringComparison.Ordinal));
    }

    [LiveFact("AI_FACTORY_LIVE_HERMES_CREDENTIALS", RequiresDocker = true)]
    public async Task PhaseMExecutor_OnlyTheFactoryKeyReachesTheProvider()
    {
        await using var provider = new RecordingProvider();
        AmbientHermesHome(); // present on the host, exactly as a developer's ~/.hermes would be
        var options = new HermesOptions
        {
            DockerImage = Image,
            WorkspaceRoot = Path.Combine(_root, "scratch"),
            NetworkMode = "bridge",
            Provider = "openrouter",
            ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
            ProviderApiKeySecretName = "OPENROUTER_API_KEY",
            TimeoutSeconds = 240,
            AdditionalEnvironmentVariables = { ["OPENROUTER_BASE_URL"] = BaseUrl(provider) }
        };
        var secrets = new InMemorySecretProvider(new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = FactoryKey });
        var executor = new HermesAgentExecutor(options, new SystemProcessRunner(), NullLogger<HermesAgentExecutor>.Instance, secretProvider: secrets);

        var result = await executor.ExecuteAsync(new AgentExecutionRequest(
            "probe", "mock/model", "Say OK.", Directory.CreateDirectory(Path.Combine(_root, "ws-new")).FullName, AgentRunId: Guid.NewGuid()));

        _output.WriteLine($"Phase M keys seen by provider: {string.Join(", ", provider.Authorizations.Distinct().Select(Mask))}");
        _output.WriteLine($"run result: success={result.Success} exit={result.ExitCode} error={result.Error}");
        Assert.False(result.Success); // the mock answers 401
        Assert.NotEmpty(provider.Authorizations);
        Assert.All(provider.Authorizations, a => Assert.Equal($"Bearer {FactoryKey}", a));
    }

    private static string Tail(string text) => text.Length <= 300 ? text : text[^300..];

    private static string Mask(string authorization) =>
        authorization.Contains(FactoryKey) ? "FACTORY key" : authorization.Contains(AmbientKey) ? "AMBIENT key" : authorization;
}
