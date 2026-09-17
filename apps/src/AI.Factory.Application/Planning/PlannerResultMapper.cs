using System.Text.Json;
using System.Text.Json.Serialization;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Agents;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Planning;

/// <summary>
/// Default <see cref="IPlannerResultMapper"/>. Reads the candidate plan out of
/// <see cref="StructuredAgentResult.Data"/> — the shape described in Phase
/// 18.6 §7: <c>{"plan":{"version":1,"summary":"...","tasks":[{"planKey":...}]}}</c>.
/// Every field is treated as untrusted: missing/malformed/wrong-typed input
/// is rejected here, never silently repaired (Phase 18.6 §9).
/// </summary>
public sealed class PlannerResultMapper : IPlannerResultMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The only candidate-plan payload version this mapper currently understands.</summary>
    public const int CurrentPlanVersion = 1;

    public ProjectPlanRequest Map(Guid projectId, StructuredAgentResult structuredResult)
    {
        if (structuredResult.Status != StructuredResultStatus.Succeeded)
        {
            throw new PlannerCandidatePlanException(
                $"Structured result status '{structuredResult.Status}' is not Succeeded — no candidate plan to map.");
        }

        if (string.IsNullOrWhiteSpace(structuredResult.Data))
        {
            throw new PlannerCandidatePlanException(
                "Structured result has no 'data' payload — expected a candidate plan under 'data.plan'.");
        }

        CandidateEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<CandidateEnvelope>(structuredResult.Data, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PlannerCandidatePlanException($"Candidate plan JSON in 'data' failed to parse: {ex.Message}");
        }

        var plan = envelope?.Plan
            ?? throw new PlannerCandidatePlanException("Structured result 'data' is missing the required 'plan' object.");

        if (plan.Version != CurrentPlanVersion)
        {
            throw new PlannerCandidatePlanException(
                $"Candidate plan declares version '{plan.Version}', only {CurrentPlanVersion} is supported.");
        }

        if (plan.Tasks is null || plan.Tasks.Count == 0)
        {
            throw new PlannerCandidatePlanException("Candidate plan has no tasks.");
        }

        var tasks = plan.Tasks.Select(MapTask).ToList();

        return new ProjectPlanRequest(projectId, plan.Summary, tasks);
    }

    private static PlannedWorkItemInput MapTask(CandidateTask task)
    {
        if (string.IsNullOrWhiteSpace(task.PlanKey))
        {
            throw new PlannerCandidatePlanException("Candidate task is missing required field 'planKey'.");
        }

        if (string.IsNullOrWhiteSpace(task.Title))
        {
            throw new PlannerCandidatePlanException($"Candidate task '{task.PlanKey}' is missing required field 'title'.");
        }

        if (string.IsNullOrWhiteSpace(task.Type)
            || !Enum.TryParse<WorkItemType>(task.Type, ignoreCase: true, out var type)
            || !Enum.IsDefined(type))
        {
            throw new PlannerCandidatePlanException(
                $"Candidate task '{task.PlanKey}' has an invalid or missing 'type' value '{task.Type}'.");
        }

        return new PlannedWorkItemInput(
            task.PlanKey,
            task.Title,
            task.Description,
            type,
            task.Priority ?? "Normal",
            task.DependsOn ?? new List<string>(),
            MapAcceptanceCriteria(task));
    }

    /// <summary>
    /// Phase 18.22 §4 — structural validation only, distinguishing the four
    /// cases the phase requires: missing, malformed, unsupported criterion,
    /// valid. Nothing here touches the filesystem: whether the declared paths
    /// exist is the acceptance subsystem's question at verification time, not
    /// the planner's at planning time. Parsed through the one shared
    /// <see cref="AcceptanceCriteriaContract"/> so the planner can never accept
    /// a contract the verifier would later refuse to read.
    /// </summary>
    private static string MapAcceptanceCriteria(CandidateTask task)
    {
        if (task.AcceptanceCriteria is not { } element || element.ValueKind == JsonValueKind.Null)
        {
            throw new PlannerCandidatePlanException(
                $"Candidate task '{task.PlanKey}' is missing required field 'acceptanceCriteria'. " +
                "Every planned task must state machine-checkable evidence of completion.",
                PlannerDiagnosticStage.PlannerAcceptanceValidation);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new PlannerCandidatePlanException(
                $"Candidate task '{task.PlanKey}' has an 'acceptanceCriteria' value that is not an object.",
                PlannerDiagnosticStage.PlannerAcceptanceValidation);
        }

        var raw = element.GetRawText();
        var parsed = AcceptanceCriteriaContract.Parse(raw);

        return parsed.Status switch
        {
            AcceptanceCriteriaParseStatus.Valid => raw,
            AcceptanceCriteriaParseStatus.Invalid => throw new PlannerCandidatePlanException(
                $"Candidate task '{task.PlanKey}' has invalid acceptance criteria: {parsed.Error}",
                PlannerDiagnosticStage.PlannerAcceptanceValidation),
            // Structurally present but not recognizable as a contract at all —
            // e.g. an object with no "criteria" array. Never treated as "none
            // specified", which would silently defeat the whole requirement.
            _ => throw new PlannerCandidatePlanException(
                $"Candidate task '{task.PlanKey}' has an 'acceptanceCriteria' object that is not a valid " +
                "acceptance contract (expected a version and a criteria array).",
                PlannerDiagnosticStage.PlannerAcceptanceValidation)
        };
    }

    private sealed class CandidateEnvelope
    {
        [JsonPropertyName("plan")]
        public CandidatePlan? Plan { get; set; }
    }

    private sealed class CandidatePlan
    {
        public int Version { get; set; }
        public string? Summary { get; set; }
        public List<CandidateTask>? Tasks { get; set; }
    }

    private sealed class CandidateTask
    {
        public string? PlanKey { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Type { get; set; }
        public string? Priority { get; set; }
        public List<string>? DependsOn { get; set; }

        /// <summary>Phase 18.22 §3 — the acceptance envelope, kept as raw JSON so it reaches storage unchanged.</summary>
        public JsonElement? AcceptanceCriteria { get; set; }
    }
}
