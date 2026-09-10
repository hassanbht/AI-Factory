using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// The one built-in, read-only <see cref="IAgentTool"/> for this phase. Takes
/// an ArtifactId (never a raw path — the agent cannot name a filesystem
/// location directly), resolves the recorded path through
/// <see cref="WorkspacePathGuard"/> against the configured Hermes workspace
/// root, and rejects secret-shaped filenames before returning content.
/// </summary>
public sealed class ReadArtifactTool : IAgentTool
{
    // ponytail: fixed 200_000-char cap keeps tool output bounded without a
    // paging API; raise or make configurable if real artifacts exceed it.
    private const int MaxContentLength = 200_000;

    private static readonly string[] DeniedNameFragments =
    {
        ".env", "id_rsa", "id_ed25519", ".pem", ".key", "credentials", ".npmrc", ".netrc", "secret"
    };

    private static readonly JsonSerializerOptions InputJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IArtifactRepository _artifactRepository;
    private readonly HermesOptions _hermesOptions;
    private readonly ILogger<ReadArtifactTool> _logger;

    public ReadArtifactTool(
        IArtifactRepository artifactRepository,
        IOptions<HermesOptions> hermesOptions,
        ILogger<ReadArtifactTool> logger)
    {
        _artifactRepository = artifactRepository;
        _hermesOptions = hermesOptions.Value;
        _logger = logger;
    }

    public string Name => "ReadArtifact";

    public string Description => "Reads the text content of a previously recorded artifact by its ArtifactId.";

    public string InputSchema =>
        """{"type":"object","properties":{"artifactId":{"type":"string","format":"uuid"}},"required":["artifactId"]}""";

    public bool Enabled => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        AgentToolInput? input;
        try
        {
            input = JsonSerializer.Deserialize<AgentToolInput>(context.InputJson, InputJsonOptions);
        }
        catch (JsonException ex)
        {
            return Failure($"Invalid input JSON: {ex.Message}", startedAt);
        }

        if (input is null || input.ArtifactId == Guid.Empty)
        {
            return Failure("Input must contain a non-empty 'artifactId'.", startedAt);
        }

        var artifact = await _artifactRepository.GetByIdAsync(input.ArtifactId, cancellationToken);
        if (artifact is null)
        {
            return Failure($"Artifact '{input.ArtifactId}' was not found.", startedAt);
        }

        var deniedFragment = DeniedNameFragments.FirstOrDefault(fragment =>
            artifact.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        if (deniedFragment is not null)
        {
            _logger.LogWarning(
                "ToolExecutionFailed: ReadArtifact refused secret-shaped artifact {ArtifactId}", artifact.Id);
            return Failure("This artifact cannot be read by an agent tool.", startedAt);
        }

        string resolvedPath;
        try
        {
            resolvedPath = WorkspacePathGuard.EnsureWithinRoot(_hermesOptions.WorkspaceRoot, artifact.Path);
        }
        catch (SecurityViolationException ex)
        {
            _logger.LogWarning(
                "ToolExecutionFailed: ReadArtifact rejected out-of-workspace path for {ArtifactId}: {Message}",
                artifact.Id, ex.Message);
            return Failure("Artifact path is outside the allowed workspace.", startedAt);
        }

        if (!File.Exists(resolvedPath))
        {
            return Failure($"Artifact file for '{input.ArtifactId}' no longer exists.", startedAt);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var content = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        if (content.Length > MaxContentLength)
        {
            content = content[..MaxContentLength];
        }

        var output = JsonSerializer.Serialize(new AgentToolOutput(artifact.Name, artifact.ArtifactType, content));
        return new AgentToolResult(true, output, null, ElapsedMs(startedAt));
    }

    private static long ElapsedMs(DateTime startedAt) => (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;

    private AgentToolResult Failure(string message, DateTime startedAt) =>
        new(false, null, message, ElapsedMs(startedAt));

    private sealed record AgentToolInput(Guid ArtifactId);

    private sealed record AgentToolOutput(string Name, string ArtifactType, string Content);
}
