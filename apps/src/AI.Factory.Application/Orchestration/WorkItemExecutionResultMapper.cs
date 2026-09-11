using System.Text.Json;
using System.Text.Json.Serialization;
using AI.Factory.Application.Agents;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Default <see cref="IWorkItemExecutionResultMapper"/>. Reads the execution
/// result out of <see cref="StructuredAgentResult.Data"/> — Phase 18.7 §5:
/// <c>{"execution":{"version":1,"status":"completed|repair_required","summary":"...","artifacts":[],"tests":[],"notes":[]}}</c>.
/// Deliberately does not persist/interpret "artifacts"/"tests"/"notes" beyond
/// shape-safety in this phase (§19 "no unnecessary migration") — only
/// "status" and "summary" influence anything, and "status" is restricted to
/// exactly two recognized values; anything else is rejected, never guessed.
/// </summary>
public sealed class WorkItemExecutionResultMapper : IWorkItemExecutionResultMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The execution payload's own version — independent of both <see cref="StructuredAgentResult.CurrentVersion"/> and the Planner's unrelated "plan.version" (Phase 18.6 §5).</summary>
    public const int CurrentExecutionVersion = 1;

    private const string CompletedStatus = "completed";
    private const string RepairRequiredStatus = "repair_required";

    public WorkItemExecutionResult Map(StructuredAgentResult structuredResult)
    {
        if (structuredResult.Status != StructuredResultStatus.Succeeded)
        {
            throw new WorkItemExecutionResultInvalidException(
                $"Structured result status '{structuredResult.Status}' is not Succeeded — no execution result to map.");
        }

        if (string.IsNullOrWhiteSpace(structuredResult.Data))
        {
            throw new WorkItemExecutionResultInvalidException(
                "Structured result has no 'data' payload — expected an execution result under 'data.execution'.");
        }

        ExecutionEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<ExecutionEnvelope>(structuredResult.Data, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new WorkItemExecutionResultInvalidException($"Execution result JSON in 'data' failed to parse: {ex.Message}");
        }

        var execution = envelope?.Execution
            ?? throw new WorkItemExecutionResultInvalidException(
                "Structured result 'data' is missing the required 'execution' object.");

        if (execution.Version != CurrentExecutionVersion)
        {
            throw new WorkItemExecutionResultInvalidException(
                $"Execution result declares version '{execution.Version}', only {CurrentExecutionVersion} is supported.");
        }

        if (string.IsNullOrWhiteSpace(execution.Status))
        {
            throw new WorkItemExecutionResultInvalidException("Execution result is missing required field 'status'.");
        }

        return execution.Status.Trim().ToLowerInvariant() switch
        {
            CompletedStatus => new WorkItemExecutionResult(WorkItemExecutionResultOutcome.ExecutionSucceeded, execution.Summary),
            RepairRequiredStatus => new WorkItemExecutionResult(WorkItemExecutionResultOutcome.ExecutionRequestedRepair, execution.Summary),
            _ => throw new WorkItemExecutionResultInvalidException(
                $"Execution result has an unrecognized 'status' value '{execution.Status}' — expected '{CompletedStatus}' or '{RepairRequiredStatus}'.")
        };
    }

    private sealed class ExecutionEnvelope
    {
        [JsonPropertyName("execution")]
        public ExecutionPayload? Execution { get; set; }
    }

    private sealed class ExecutionPayload
    {
        public int Version { get; set; }
        public string? Status { get; set; }
        public string? Summary { get; set; }
        public List<string>? Artifacts { get; set; }
        public List<string>? Tests { get; set; }
        public List<string>? Notes { get; set; }
    }
}
