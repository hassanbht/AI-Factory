using System.Text.Json;
using System.Text.Json.Serialization;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.21 §4 — the supported, deliberately small set of executable
/// criterion types. Every one of them is evaluated by Factory-owned code
/// against files on disk; none of them carries a command, script or pattern
/// language, so an acceptance contract can never become an injection vector
/// no matter who (planner, operator, or an agent editing a plan) authored it.
/// </summary>
public enum AcceptanceCriterionType
{
    /// <summary>The file at Path must exist.</summary>
    FileExists,

    /// <summary>The file at Path must not exist.</summary>
    FileAbsent,

    /// <summary>The file at Path must contain Value (ordinal substring).</summary>
    ExpectedTextPresent,

    /// <summary>The file at Path must NOT contain Value (ordinal substring).</summary>
    ExpectedTextAbsent,

    /// <summary>The file at Path must equal Value exactly, ignoring surrounding whitespace and line-ending style.</summary>
    ExpectedFileContent,

    /// <summary>
    /// Satisfied by construction: IVerificationGate already ran a real build
    /// before acceptance is evaluated at all, and acceptance is only reached
    /// when it passed. Recognized so a plan may state it explicitly without
    /// being rejected as unsupported.
    /// </summary>
    BuildPasses,

    /// <summary>Satisfied by construction, for the same reason as <see cref="BuildPasses"/>.</summary>
    TestsPass
}

/// <param name="Type">Which deterministic check to run.</param>
/// <param name="Path">Workspace-relative file path. Required except for BuildPasses/TestsPass.</param>
/// <param name="Value">Expected text. Required for the text/content criteria.</param>
public sealed record AcceptanceCriterion(AcceptanceCriterionType Type, string? Path, string? Value);

/// <summary>
/// Phase 18.21 §5/§6 — the Factory's answer to "did the requested outcome
/// actually happen", evaluated against the same resolved workspace the agent
/// wrote to and the build/test gate judged.
///
/// Criteria travel as JSON in the pre-existing nullable
/// <see cref="WorkItem.AcceptanceCriteria"/> column (§23 — no migration):
/// <code>
/// {"version":1,"criteria":[
///   {"type":"ExpectedTextPresent","path":"src/Marker.cs","value":"MARKER_AFTER"},
///   {"type":"ExpectedTextAbsent","path":"src/Marker.cs","value":"MARKER_BEFORE"}
/// ]}
/// </code>
/// Anything that is not that envelope — empty, or the free text historical
/// WorkItems already hold in this column — is reported as
/// <see cref="AcceptanceVerificationStatus.NotSpecified"/> rather than being
/// guessed at (§9).
/// </summary>
public sealed class WorkItemAcceptanceVerifier : IWorkItemAcceptanceVerifier
{
    public const int CurrentVersion = 1;

    private const int MaxSummaryLength = 2000;
    private const int MaxFileBytes = 1_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<WorkItemAcceptanceVerifier> _logger;

    public WorkItemAcceptanceVerifier(ILogger<WorkItemAcceptanceVerifier> logger)
    {
        _logger = logger;
    }

    public AcceptanceVerificationResult Verify(WorkItem workItem, string workspacePath)
    {
        var raw = workItem.AcceptanceCriteria;

        if (string.IsNullOrWhiteSpace(raw) || !LooksLikeEnvelope(raw))
        {
            // Legacy free text (or nothing at all) is not a machine-checkable
            // contract and must never be reinterpreted as one.
            return new AcceptanceVerificationResult(AcceptanceVerificationStatus.NotSpecified, null, 0);
        }

        AcceptanceCriteriaEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<AcceptanceCriteriaEnvelope>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Invalid($"Acceptance criteria JSON failed to parse: {ex.Message}");
        }

        if (envelope is null)
        {
            return Invalid("Acceptance criteria JSON parsed to null.");
        }

        if (envelope.Version != CurrentVersion)
        {
            return Invalid($"Acceptance criteria declare version '{envelope.Version}', only {CurrentVersion} is supported.");
        }

        if (envelope.Criteria is null || envelope.Criteria.Count == 0)
        {
            return Invalid("Acceptance criteria envelope contains no criteria.");
        }

        var failures = new List<string>();
        var evaluated = 0;

        foreach (var dto in envelope.Criteria)
        {
            if (!Enum.TryParse<AcceptanceCriterionType>(dto.Type, ignoreCase: true, out var type)
                || !Enum.IsDefined(type))
            {
                return Invalid($"Unsupported acceptance criterion type '{dto.Type}'.");
            }

            evaluated++;

            // Already proven by the build/test gate that ran before this.
            if (type is AcceptanceCriterionType.BuildPasses or AcceptanceCriterionType.TestsPass)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(dto.Path))
            {
                return Invalid($"Acceptance criterion '{type}' is missing the required 'path'.");
            }

            var needsValue = type is AcceptanceCriterionType.ExpectedTextPresent
                or AcceptanceCriterionType.ExpectedTextAbsent
                or AcceptanceCriterionType.ExpectedFileContent;

            if (needsValue && string.IsNullOrEmpty(dto.Value))
            {
                return Invalid($"Acceptance criterion '{type}' for '{dto.Path}' is missing the required 'value'.");
            }

            string resolvedPath;

            try
            {
                // The contract is untrusted input like any other: a criterion
                // must never be able to read outside the Project workspace.
                resolvedPath = WorkspacePathGuard.EnsureWithinRoot(workspacePath, dto.Path!);
            }
            catch (SecurityViolationException ex)
            {
                return Invalid($"Acceptance criterion path '{dto.Path}' is not allowed: {ex.Message}");
            }

            var failure = Evaluate(type, dto, resolvedPath);

            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            var summary = Truncate(
                $"Acceptance criteria not met ({failures.Count} of {evaluated}): {string.Join("; ", failures)}");

            _logger.LogInformation(
                "WorkItemAcceptanceFailed WorkItemId={WorkItemId} FailedCriteria={FailedCount} EvaluatedCriteria={EvaluatedCount}",
                workItem.Id, failures.Count, evaluated);

            return new AcceptanceVerificationResult(AcceptanceVerificationStatus.Failed, summary, evaluated);
        }

        _logger.LogInformation(
            "WorkItemAcceptancePassed WorkItemId={WorkItemId} EvaluatedCriteria={EvaluatedCount}",
            workItem.Id, evaluated);

        return new AcceptanceVerificationResult(
            AcceptanceVerificationStatus.Passed, $"All {evaluated} acceptance criteria met.", evaluated);
    }

    private string? Evaluate(AcceptanceCriterionType type, AcceptanceCriterionDto dto, string resolvedPath)
    {
        var exists = File.Exists(resolvedPath);

        if (type == AcceptanceCriterionType.FileExists)
        {
            return exists ? null : $"expected file '{dto.Path}' to exist";
        }

        if (type == AcceptanceCriterionType.FileAbsent)
        {
            return exists ? $"expected file '{dto.Path}' to be absent" : null;
        }

        if (!exists)
        {
            return $"file '{dto.Path}' does not exist";
        }

        string content;

        try
        {
            var info = new FileInfo(resolvedPath);

            if (info.Length > MaxFileBytes)
            {
                return $"file '{dto.Path}' is too large to verify ({info.Length} bytes)";
            }

            content = File.ReadAllText(resolvedPath);
        }
        catch (IOException ex)
        {
            return $"file '{dto.Path}' could not be read: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"file '{dto.Path}' could not be read: {ex.Message}";
        }

        return type switch
        {
            AcceptanceCriterionType.ExpectedTextPresent when !content.Contains(dto.Value!, StringComparison.Ordinal) =>
                $"expected '{dto.Path}' to contain \"{Truncate(dto.Value!, 120)}\"",
            AcceptanceCriterionType.ExpectedTextAbsent when content.Contains(dto.Value!, StringComparison.Ordinal) =>
                $"expected '{dto.Path}' to no longer contain \"{Truncate(dto.Value!, 120)}\"",
            AcceptanceCriterionType.ExpectedFileContent when !ContentEquals(content, dto.Value!) =>
                $"expected '{dto.Path}' to contain exactly \"{Truncate(dto.Value!, 120)}\"",
            _ => null
        };
    }

    /// <summary>Line-ending style and surrounding whitespace are editor noise, not outcome differences.</summary>
    private static bool ContentEquals(string actual, string expected) =>
        string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal);

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    /// <summary>
    /// Only text that is actually shaped like the envelope is treated as a
    /// contract; everything else is legacy free text (§9). Deliberately a
    /// cheap structural check — the real parse still has to succeed.
    /// </summary>
    private static bool LooksLikeEnvelope(string raw)
    {
        var trimmed = raw.TrimStart();
        return trimmed.StartsWith('{') && raw.Contains("\"criteria\"", StringComparison.OrdinalIgnoreCase);
    }

    private AcceptanceVerificationResult Invalid(string reason)
    {
        _logger.LogWarning("WorkItemAcceptanceContractInvalid Reason={Reason}", reason);
        return new AcceptanceVerificationResult(AcceptanceVerificationStatus.ContractInvalid, Truncate(reason), 0);
    }

    private static string Truncate(string text, int max = MaxSummaryLength) =>
        text.Length <= max ? text : text[..max] + "…(truncated)";

    private sealed class AcceptanceCriteriaEnvelope
    {
        public int Version { get; set; }

        public List<AcceptanceCriterionDto>? Criteria { get; set; }
    }

    private sealed class AcceptanceCriterionDto
    {
        public string? Type { get; set; }

        public string? Path { get; set; }

        public string? Value { get; set; }
    }
}
