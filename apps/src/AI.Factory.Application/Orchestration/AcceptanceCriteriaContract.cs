using System.Text.Json;
using System.Text.Json.Serialization;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.22 §3/§4 — the single parser for the acceptance envelope
/// introduced in Phase 18.21. Two callers need the same answer at different
/// times and must never drift into two formats:
/// <list type="bullet">
/// <item><see cref="PlannerResultMapper"/> asks "is this contract structurally
/// usable?" while a plan is being accepted, before anything is persisted.</item>
/// <item><see cref="WorkItemAcceptanceVerifier"/> asks the same question and
/// then evaluates the criteria against the real workspace.</item>
/// </list>
/// Structure only: nothing here touches the filesystem, so a planner's plan is
/// never accepted or rejected because of what does or does not happen to exist
/// on disk at planning time (§4).
/// </summary>
public static class AcceptanceCriteriaContract
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AcceptanceCriteriaParseResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !LooksLikeEnvelope(raw))
        {
            // Empty, or the free text historical WorkItems already hold in this
            // column. Not machine-checkable, and never guessed at.
            return AcceptanceCriteriaParseResult.NotSpecified();
        }

        AcceptanceCriteriaEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<AcceptanceCriteriaEnvelope>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            return AcceptanceCriteriaParseResult.Invalid($"Acceptance criteria JSON failed to parse: {ex.Message}");
        }

        if (envelope is null)
        {
            return AcceptanceCriteriaParseResult.Invalid("Acceptance criteria JSON parsed to null.");
        }

        if (envelope.Version != CurrentVersion)
        {
            return AcceptanceCriteriaParseResult.Invalid(
                $"Acceptance criteria declare version '{envelope.Version}', only {CurrentVersion} is supported.");
        }

        if (envelope.Criteria is null || envelope.Criteria.Count == 0)
        {
            return AcceptanceCriteriaParseResult.Invalid("Acceptance criteria envelope contains no criteria.");
        }

        var criteria = new List<AcceptanceCriterion>(envelope.Criteria.Count);

        foreach (var dto in envelope.Criteria)
        {
            if (!Enum.TryParse<AcceptanceCriterionType>(dto.Type, ignoreCase: true, out var type)
                || !Enum.IsDefined(type))
            {
                return AcceptanceCriteriaParseResult.Invalid($"Unsupported acceptance criterion type '{dto.Type}'.");
            }

            // BuildPasses/TestsPass are satisfied by construction — the
            // build/test gate has already run and passed before acceptance is
            // evaluated — so they carry no path or value.
            if (type is AcceptanceCriterionType.BuildPasses or AcceptanceCriterionType.TestsPass)
            {
                criteria.Add(new AcceptanceCriterion(type, null, null));
                continue;
            }

            if (string.IsNullOrWhiteSpace(dto.Path))
            {
                return AcceptanceCriteriaParseResult.Invalid(
                    $"Acceptance criterion '{type}' is missing the required 'path'.");
            }

            var needsValue = type is AcceptanceCriterionType.ExpectedTextPresent
                or AcceptanceCriterionType.ExpectedTextAbsent
                or AcceptanceCriterionType.ExpectedFileContent;

            if (needsValue && string.IsNullOrEmpty(dto.Value))
            {
                return AcceptanceCriteriaParseResult.Invalid(
                    $"Acceptance criterion '{type}' for '{dto.Path}' is missing the required 'value'.");
            }

            criteria.Add(new AcceptanceCriterion(type, dto.Path, dto.Value));
        }

        return AcceptanceCriteriaParseResult.Valid(criteria);
    }

    /// <summary>
    /// Only text actually shaped like the envelope is treated as a contract;
    /// everything else is legacy free text (Phase 18.21 §9). Deliberately a
    /// cheap structural check — the real parse still has to succeed.
    /// </summary>
    private static bool LooksLikeEnvelope(string raw)
    {
        var trimmed = raw.TrimStart();
        return trimmed.StartsWith('{') && raw.Contains("\"criteria\"", StringComparison.OrdinalIgnoreCase);
    }

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

public enum AcceptanceCriteriaParseStatus
{
    /// <summary>No machine-checkable contract: empty, or legacy free text.</summary>
    NotSpecified,

    /// <summary>A well-formed contract with at least one supported criterion.</summary>
    Valid,

    /// <summary>Shaped like a contract but unusable — malformed, wrong version, unsupported type, missing fields.</summary>
    Invalid
}

public sealed record AcceptanceCriteriaParseResult(
    AcceptanceCriteriaParseStatus Status,
    IReadOnlyList<AcceptanceCriterion> Criteria,
    string? Error)
{
    public static AcceptanceCriteriaParseResult NotSpecified() =>
        new(AcceptanceCriteriaParseStatus.NotSpecified, Array.Empty<AcceptanceCriterion>(), null);

    public static AcceptanceCriteriaParseResult Valid(IReadOnlyList<AcceptanceCriterion> criteria) =>
        new(AcceptanceCriteriaParseStatus.Valid, criteria, null);

    public static AcceptanceCriteriaParseResult Invalid(string error) =>
        new(AcceptanceCriteriaParseStatus.Invalid, Array.Empty<AcceptanceCriterion>(), error);
}
