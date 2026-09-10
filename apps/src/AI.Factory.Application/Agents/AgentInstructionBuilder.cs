using System.Text;

namespace AI.Factory.Application.Agents;

/// <summary>
/// Default <see cref="IAgentInstructionBuilder"/>. Deliberately generic — the
/// same shape for every agent today, since Planner/Developer/Reviewer-specific
/// behavior does not exist yet (Phase 18.1.8 §6/§13). Always ends with an
/// explicit output-contract section so the agent knows exactly what
/// <see cref="IStructuredAgentResultParser"/> expects back.
/// </summary>
public sealed class AgentInstructionBuilder : IAgentInstructionBuilder
{
    public string Build(AgentExecutionInstruction instruction)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Objective:");
        sb.AppendLine(instruction.Objective);
        sb.AppendLine();

        if (instruction.Instructions.Count > 0)
        {
            sb.AppendLine("Instructions:");
            foreach (var line in instruction.Instructions)
            {
                sb.Append("- ").AppendLine(line);
            }
            sb.AppendLine();
        }

        if (instruction.Constraints.Count > 0)
        {
            sb.AppendLine("Constraints:");
            foreach (var constraint in instruction.Constraints)
            {
                sb.Append("- ").AppendLine(constraint);
            }
            sb.AppendLine();
        }

        if (instruction.AllowedCapabilities.Count > 0)
        {
            sb.Append("Available capabilities: ")
                .AppendLine(string.Join(", ", instruction.AllowedCapabilities));
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(instruction.ProjectContext))
        {
            sb.AppendLine("Project context:");
            sb.AppendLine(instruction.ProjectContext);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(instruction.PreviousContext))
        {
            sb.AppendLine("Relevant prior context:");
            sb.AppendLine(instruction.PreviousContext);
            sb.AppendLine();
        }

        sb.AppendLine("Output contract:");
        sb.AppendLine("Respond with exactly one JSON object and nothing else (no prose before or after it), matching this shape:");
        sb.AppendLine(
            """{"version":1,"status":"Succeeded|Failed|Cancelled|TimedOut","summary":"<one-line summary>","output":"<your textual response>","data":{},"warnings":[],"errors":[],"artifacts":[],"metadata":{}}""");
        sb.AppendLine("- \"version\" must be the number 1.");
        sb.AppendLine("- \"status\" must reflect whether you actually completed the objective above.");
        sb.AppendLine($"- Expected response format: {instruction.ExpectedOutputFormat}.");
        sb.AppendLine($"- Correlation id: {instruction.CorrelationId}.");

        return sb.ToString().TrimEnd();
    }
}
