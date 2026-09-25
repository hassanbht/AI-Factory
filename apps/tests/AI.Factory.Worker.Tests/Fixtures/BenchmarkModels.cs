using System.Text.Json;
using System.Text.Json.Serialization;

namespace AI.Factory.Worker.Tests.Fixtures;

/// <summary>
/// Phase F — Benchmark data models for empirical Hermes vs OpenHands comparison.
/// Explicit, provider-neutral comparison record outside the production database schema.
/// </summary>
public sealed class BenchmarkRunRecord
{
    public string BenchmarkId { get; set; } = string.Empty;
    public string Runtime { get; set; } = string.Empty; // "Hermes" | "OpenHands"
    public Guid RunId { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid ProjectId { get; set; }
    public string SourceCommit { get; set; } = string.Empty;
    public string BaselineHash { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public double ExecutionDurationSeconds { get; set; }
    public string TerminalStatus { get; set; } = string.Empty;
    public string? FailureCategory { get; set; }
    public string? ExitReason { get; set; }
    public int AttemptNumber { get; set; } = 1;
    public int RetryCount { get; set; }
    public int RepairCount { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public string AcceptanceStatus { get; set; } = "NotRun"; // "Passed" | "Failed"
    public double VerificationDurationSeconds { get; set; }
    public string ReviewStatus { get; set; } = "NotRun";
    public List<string> ChangedFiles { get; set; } = new();
    public Dictionary<string, string> FileClassifications { get; set; } = new(); // Required, AcceptableIncidental, Unexpected, Harmful
    public int ArtifactCount { get; set; }
    public bool CleanupVerified { get; set; }
    public string DiffSummary { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static BenchmarkRunRecord? FromJson(string json) =>
        JsonSerializer.Deserialize<BenchmarkRunRecord>(json, JsonOptions);
}

public sealed class BenchmarkComparisonSummary
{
    public string BenchmarkId { get; set; } = "BENCH-001";
    public string Model { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public string CanonicalSourceCommit { get; set; } = string.Empty;
    public string Validity { get; set; } = "CONTROLLED"; // "CONTROLLED" | "LIMITED" | "INVALID"
    public string ValidityReason { get; set; } = string.Empty;

    public List<BenchmarkRunRecord> HermesRuns { get; set; } = new();
    public List<BenchmarkRunRecord> OpenHandsRuns { get; set; } = new();

    public static double CalculateMedian(IEnumerable<double> values)
    {
        var list = values.OrderBy(v => v).ToList();
        if (list.Count == 0) return 0;
        int mid = list.Count / 2;
        return (list.Count % 2 != 0) ? list[mid] : (list[mid - 1] + list[mid]) / 2.0;
    }
}
