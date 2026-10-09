using System.Text.Json;
using AI.Factory.Application.Agents;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Provider-neutral parser for an untrusted WorkItem execution report. The
/// report status is deliberately narrow; artifact/test metadata is normalized
/// while the raw execution object is retained for audit evidence.
/// </summary>
public sealed class WorkItemExecutionResultMapper : IWorkItemExecutionResultMapper
{
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

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(structuredResult.Data);
        }
        catch (JsonException ex)
        {
            throw new WorkItemExecutionResultInvalidException($"Execution result JSON in 'data' failed to parse: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(document.RootElement, "execution", out var execution)
                || execution.ValueKind != JsonValueKind.Object)
            {
                throw new WorkItemExecutionResultInvalidException(
                    "Structured result 'data' is missing the required 'execution' object.");
            }

            var payload = DeserializePayload(execution);
            if (payload.Version != CurrentExecutionVersion)
            {
                throw new WorkItemExecutionResultInvalidException(
                    $"Execution result declares version '{payload.Version}', only {CurrentExecutionVersion} is supported.");
            }

            if (string.IsNullOrWhiteSpace(payload.Status))
            {
                throw new WorkItemExecutionResultInvalidException("Execution result is missing required field 'status'.");
            }

            var normalizedStatus = payload.Status.Trim().ToLowerInvariant();
            var outcome = normalizedStatus switch
            {
                CompletedStatus => WorkItemExecutionResultOutcome.ExecutionSucceeded,
                RepairRequiredStatus => WorkItemExecutionResultOutcome.ExecutionRequestedRepair,
                _ => throw new WorkItemExecutionResultInvalidException(
                    $"Execution result has an unrecognized 'status' value '{payload.Status}' — expected '{CompletedStatus}' or '{RepairRequiredStatus}'.")
            };

            return new WorkItemExecutionResult(
                outcome,
                payload.Summary,
                execution.GetRawText(),
                payload.Tests,
                payload.Artifacts,
                payload.Notes);
        }
    }

    private static ExecutionPayload DeserializePayload(JsonElement execution)
    {
        try
        {
            var version = TryGetPropertyIgnoreCase(execution, "version", out var versionElement)
                && versionElement.TryGetInt32(out var parsedVersion)
                    ? parsedVersion
                    : 0;
            var status = GetOptionalString(execution, "status");
            var summary = GetOptionalString(execution, "summary");
            var tests = ParseTests(execution);
            var artifacts = ParseStringList(execution, "artifacts");
            var notes = ParseStringList(execution, "notes");
            return new ExecutionPayload(version, status, summary, tests, artifacts, notes);
        }
        catch (WorkItemExecutionResultInvalidException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new WorkItemExecutionResultInvalidException($"Execution result has an invalid field: {ex.Message}");
        }
    }

    private static string? GetOptionalString(JsonElement owner, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(owner, propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new WorkItemExecutionResultInvalidException($"Execution result field '{propertyName}' must be a string.");
        }

        return value.GetString();
    }

    private static IReadOnlyList<ExecutionTestResult> ParseTests(JsonElement execution)
    {
        if (!TryGetPropertyIgnoreCase(execution, "tests", out var tests) || tests.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<ExecutionTestResult>();
        }

        if (tests.ValueKind != JsonValueKind.Array)
        {
            throw new WorkItemExecutionResultInvalidException("Execution result field 'tests' must be an array.");
        }

        var parsed = new List<ExecutionTestResult>();
        foreach (var test in tests.EnumerateArray())
        {
            if (test.ValueKind == JsonValueKind.String)
            {
                var legacy = test.GetString()!;
                parsed.Add(new ExecutionTestResult(legacy, null));
                continue;
            }

            if (test.ValueKind != JsonValueKind.Object)
            {
                throw new WorkItemExecutionResultInvalidException(
                    "Each execution test entry must be a string or an object with string 'name' and optional string 'result'.");
            }

            var name = GetOptionalString(test, "name");
            var result = GetOptionalString(test, "result");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new WorkItemExecutionResultInvalidException("Each object-shaped execution test entry requires a non-empty 'name'.");
            }

            parsed.Add(new ExecutionTestResult(name, result));
        }

        return parsed;
    }

    private static IReadOnlyList<string> ParseStringList(JsonElement execution, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(execution, propertyName, out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            throw new WorkItemExecutionResultInvalidException($"Execution result field '{propertyName}' must be an array.");
        }

        var parsed = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new WorkItemExecutionResultInvalidException(
                    $"Every value in execution result field '{propertyName}' must be a string.");
            }

            parsed.Add(item.GetString()!);
        }

        return parsed;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement owner, string propertyName, out JsonElement value)
    {
        foreach (var property in owner.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed record ExecutionPayload(
        int Version,
        string? Status,
        string? Summary,
        IReadOnlyList<ExecutionTestResult> Tests,
        IReadOnlyList<string> Artifacts,
        IReadOnlyList<string> Notes);
}
