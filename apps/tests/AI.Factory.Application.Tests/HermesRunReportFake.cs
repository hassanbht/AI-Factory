using System.Text.Json;
using AI.Factory.Infrastructure.Agents;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase L — writes the `--usage-file` run report a scripted Hermes `docker run` would leave in
/// the mounted workspace. Hermes 0.20.6 writes it on every terminal path; since Phase L the
/// executor treats exit 0 without a "completed" report as unproven, so fakes that simulate a
/// successful Hermes run must write one, exactly as the real CLI does.
/// </summary>
internal static class HermesRunReportFake
{
    public static void Write(ProcessRunRequest request, bool failed = false, bool completed = true) =>
        WriteRaw(request, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["failed"] = failed,
            ["completed"] = completed,
            ["provider"] = "scripted"
        }));

    /// <summary>Writes <paramref name="content"/> verbatim as the report; null writes nothing.</summary>
    public static void WriteRaw(ProcessRunRequest request, string? content)
    {
        var args = request.Arguments.ToList();
        var index = args.IndexOf("--usage-file");
        if (content is null || index < 0 || string.IsNullOrEmpty(request.WorkingDirectory))
        {
            return;
        }

        File.WriteAllText(Path.Combine(request.WorkingDirectory, args[index + 1]["/workspace/".Length..]), content);
    }
}
