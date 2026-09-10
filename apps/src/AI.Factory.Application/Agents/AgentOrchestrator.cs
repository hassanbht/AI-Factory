using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Agents;

/// <summary>
/// The one Factory-owned implementation of <see cref="IAgentOrchestrator"/>.
/// Coordinates a single AgentRun end to end: state-machine eligibility check,
/// AgentDefinition load, tool/memory resolution, a single <see cref="IAgentRuntime"/>
/// call, then the resulting status transition — all through existing
/// abstractions. It is not itself a runtime: it never touches Docker,
/// processes, or Hermes, and it never bypasses AgentRun.TransitionTo's
/// centralized validation.
/// </summary>
public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IAgentRunRepository _agentRunRepository;
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly IAgentMemory _memory;
    private readonly IAgentRuntime _runtime;
    private readonly IAgentInstructionBuilder _instructionBuilder;
    private readonly IStructuredAgentResultParser _structuredResultParser;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        IAgentRunRepository agentRunRepository,
        IAgentToolRegistry toolRegistry,
        IAgentMemory memory,
        IAgentRuntime runtime,
        IAgentInstructionBuilder instructionBuilder,
        IStructuredAgentResultParser structuredResultParser,
        ILogger<AgentOrchestrator> logger)
    {
        _agentRunRepository = agentRunRepository;
        _toolRegistry = toolRegistry;
        _memory = memory;
        _runtime = runtime;
        _instructionBuilder = instructionBuilder;
        _structuredResultParser = structuredResultParser;
        _logger = logger;
    }

    public async Task<AgentOrchestrationResult> ExecuteAsync(
        Guid agentRunId,
        CancellationToken cancellationToken = default)
    {
        var run = await _agentRunRepository.GetByIdAsync(agentRunId, cancellationToken);

        if (run is null)
        {
            _logger.LogWarning("AgentOrchestratorSkipped AgentRunId={AgentRunId} Reason=NotFound", agentRunId);
            return NotExecuted(null, "AgentRun was not found.");
        }

        if (AgentRunTransitions.IsTerminal(run.Status))
        {
            _logger.LogInformation(
                "AgentOrchestratorSkipped AgentRunId={AgentRunId} Reason=AlreadyTerminal Status={Status}",
                run.Id, run.Status);
            return NotExecuted(run, $"AgentRun is already in terminal state '{run.Status}'.");
        }

        // Running is a *valid* entry state, not a reason to skip: the atomic
        // claim (IAgentRunRepository.TryClaimNextPendingAsync, or this same
        // Queued/Waiting -> Running transition below) is what guarantees a
        // single winner — by the time a caller legitimately holds a Running
        // AgentRun, re-checking "is it Running" and skipping would mean the
        // Worker's own claim-then-execute sequence could never execute
        // anything (its own claim always leaves the run Running before this
        // method is ever called). Proceed straight to execution for Running.
        if (run.Status is AgentRunStatus.Queued or AgentRunStatus.Waiting)
        {
            try
            {
                run.TransitionTo(AgentRunStatus.Running);
                await _agentRunRepository.UpdateAsync(run, cancellationToken);
            }
            catch (ConcurrencyConflictException)
            {
                _logger.LogInformation("AgentOrchestratorSkipped AgentRunId={AgentRunId} Reason=ConcurrentClaim", run.Id);
                return NotExecuted(run, "AgentRun was claimed by another execution.");
            }
        }
        else if (run.Status != AgentRunStatus.Running)
        {
            _logger.LogWarning(
                "AgentOrchestratorRejected AgentRunId={AgentRunId} Reason=InvalidState Status={Status}",
                run.Id, run.Status);
            return NotExecuted(run, $"AgentRun status '{run.Status}' is not eligible for execution.");
        }

        _logger.LogInformation(
            "AgentRunStarted AgentRunId={AgentRunId} AgentDefinitionId={AgentDefinitionId} ProjectId={ProjectId} CorrelationId={CorrelationId}",
            run.Id, run.AgentDefinitionId, run.ProjectId, run.CorrelationId);

        if (run.AgentDefinition is null)
        {
            return await FailRunningAsync(
                run, "AgentDefinition could not be loaded for this AgentRun.", FailureCategory.Validation, cancellationToken);
        }

        var availableToolNames = _toolRegistry.GetDescriptors().Select(x => x.Name).ToArray();

        var memoryEntries = await SearchMemorySafelyAsync(run, cancellationToken);
        var executionRun = BuildExecutionSnapshot(run, availableToolNames, memoryEntries);

        var runtimeResult = await _runtime.ExecuteAsync(executionRun, cancellationToken);

        var finalStatus = runtimeResult.Success
            ? AgentRunStatus.Succeeded
            : MapFailureStatus(runtimeResult.FailureCategory);

        var structuredResult = BuildStructuredResult(runtimeResult, finalStatus);

        run.Output = runtimeResult.Output;
        run.ErrorMessage = runtimeResult.ErrorMessage;
        run.FailureCategory = runtimeResult.FailureCategory;
        run.PromptTokens = runtimeResult.PromptTokens;
        run.CompletionTokens = runtimeResult.CompletionTokens;
        run.TotalTokens = runtimeResult.TotalTokens;
        run.StructuredResult = JsonSerializer.Serialize(structuredResult, StructuredAgentResult.JsonOptions);
        run.StructuredResultVersion = structuredResult.Version;
        run.TransitionTo(finalStatus);

        // The outcome (including Cancelled/TimedOut) is already decided —
        // persisting it must not itself be abortable by the same token that
        // just cancelled the run, or a cancelled/timed-out AgentRun would be
        // left stuck at Running forever with no terminal status ever saved.
        await _agentRunRepository.UpdateAsync(run, CancellationToken.None);

        if (runtimeResult.Success)
        {
            _logger.LogInformation(
                "AgentRunCompleted AgentRunId={AgentRunId} DurationMs={DurationMs}", run.Id, runtimeResult.DurationMs);
        }
        else
        {
            _logger.LogWarning(
                "AgentRun{FinalStatus} AgentRunId={AgentRunId} DurationMs={DurationMs} FailureCategory={FailureCategory}",
                finalStatus, run.Id, runtimeResult.DurationMs, runtimeResult.FailureCategory);
        }

        return new AgentOrchestrationResult(
            Executed: true,
            Success: runtimeResult.Success,
            FinalStatus: finalStatus,
            Output: runtimeResult.Output,
            DurationMs: runtimeResult.DurationMs,
            ToolCallNames: runtimeResult.ToolCallNames,
            AvailableToolNames: availableToolNames,
            ErrorMessage: runtimeResult.ErrorMessage,
            FailureCategory: runtimeResult.FailureCategory,
            CorrelationId: run.CorrelationId);
    }

    /// <summary>Memory is contextual, not required — a memory-provider failure must never block execution.</summary>
    private async Task<IReadOnlyList<AgentMemoryEntry>> SearchMemorySafelyAsync(
        AgentRun run, CancellationToken cancellationToken)
    {
        try
        {
            var query = new AgentMemoryQuery(ProjectId: run.ProjectId, AgentDefinitionId: run.AgentDefinitionId);
            return await _memory.SearchAsync(query, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentOrchestratorMemorySearchFailed AgentRunId={AgentRunId}", run.Id);
            return Array.Empty<AgentMemoryEntry>();
        }
    }

    /// <summary>
    /// Builds a non-persisted execution-time copy of the AgentRun whose Input
    /// is the full instruction text from <see cref="IAgentInstructionBuilder"/>
    /// (objective, constraints, capabilities, prior-memory context, and the
    /// structured-output contract) — never ad-hoc string concatenation here.
    /// Kept as an AgentRun (rather than a dedicated context type) so
    /// IAgentRuntime's existing contract needs no change; only ever passed to
    /// the runtime, never saved.
    /// ponytail: revisit as a dedicated execution-context contract if the
    /// runtime ever needs more than Input augmented.
    /// </summary>
    private AgentRun BuildExecutionSnapshot(
        AgentRun run, IReadOnlyList<string> availableToolNames, IReadOnlyList<AgentMemoryEntry> memoryEntries)
    {
        var previousContext = memoryEntries.Count == 0
            ? null
            : string.Join("\n", memoryEntries.Select(x => $"- [{x.Type}] {x.Content}"));

        var instruction = new AgentExecutionInstruction(
            CorrelationId: run.CorrelationId,
            Objective: run.Input ?? string.Empty,
            Instructions: Array.Empty<string>(),
            ProjectContext: run.ProjectId is { } projectId ? $"ProjectId: {projectId}" : null,
            Constraints: Array.Empty<string>(),
            ExpectedOutputFormat: "structured-json-v1",
            AllowedCapabilities: availableToolNames,
            PreviousContext: previousContext);

        var instructionText = _instructionBuilder.Build(instruction);

        return new AgentRun
        {
            Id = run.Id,
            AgentDefinitionId = run.AgentDefinitionId,
            AgentDefinition = run.AgentDefinition,
            ProjectId = run.ProjectId,
            WorkItemId = run.WorkItemId,
            Status = run.Status,
            Iteration = run.Iteration,
            Attempt = run.Attempt,
            CorrelationId = run.CorrelationId,
            Provider = run.Provider,
            Model = run.Model,
            Input = instructionText,
            CreatedAtUtc = run.CreatedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc,
            Version = run.Version
        };
    }

    /// <summary>
    /// Parses the runtime's raw output into a structured result on success;
    /// on failure/cancellation/timeout builds a deterministic structured
    /// result directly from the already-known outcome rather than attempting
    /// to parse LLM text that was never produced (or is irrelevant).
    /// </summary>
    private StructuredAgentResult BuildStructuredResult(AgentRunExecutionResult runtimeResult, AgentRunStatus finalStatus)
    {
        if (!runtimeResult.Success)
        {
            return new StructuredAgentResult(
                Version: StructuredAgentResult.CurrentVersion,
                Status: MapToStructuredStatus(finalStatus),
                Summary: null,
                Output: runtimeResult.Output,
                Data: null,
                Warnings: Array.Empty<string>(),
                Errors: new[] { runtimeResult.ErrorMessage ?? "Agent execution failed with no further detail." },
                Artifacts: runtimeResult.ArtifactReferences,
                Metadata: null);
        }

        var parseOutcome = _structuredResultParser.Parse(runtimeResult.Output);

        if (parseOutcome.IsValid)
        {
            return parseOutcome.Result!;
        }

        // The runtime call itself succeeded (real completion, real exit code
        // 0) but the agent's own output did not honor the structured-output
        // contract — never silently accepted (Phase 18.1.8 §4). AgentRun's
        // own Status is untouched by this; it stays whatever the runtime
        // reported.
        _logger.LogWarning(
            "StructuredAgentResultInvalid Reason={Reason} Message={Message}",
            parseOutcome.FailureReason, parseOutcome.Message);

        return new StructuredAgentResult(
            Version: StructuredAgentResult.CurrentVersion,
            Status: StructuredResultStatus.Invalid,
            Summary: null,
            Output: runtimeResult.Output,
            Data: null,
            Warnings: new[] { parseOutcome.Message ?? "Structured result could not be validated." },
            Errors: Array.Empty<string>(),
            Artifacts: runtimeResult.ArtifactReferences,
            Metadata: null);
    }

    private static StructuredResultStatus MapToStructuredStatus(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Succeeded => StructuredResultStatus.Succeeded,
        AgentRunStatus.TimedOut => StructuredResultStatus.TimedOut,
        AgentRunStatus.Cancelled => StructuredResultStatus.Cancelled,
        _ => StructuredResultStatus.Failed
    };

    private static AgentRunStatus MapFailureStatus(FailureCategory? category) => category switch
    {
        FailureCategory.Timeout => AgentRunStatus.TimedOut,
        FailureCategory.Cancelled => AgentRunStatus.Cancelled,
        _ => AgentRunStatus.Failed
    };

    private async Task<AgentOrchestrationResult> FailRunningAsync(
        AgentRun run, string message, FailureCategory category, CancellationToken cancellationToken)
    {
        run.ErrorMessage = message;
        run.FailureCategory = category;

        var structuredResult = new StructuredAgentResult(
            Version: StructuredAgentResult.CurrentVersion,
            Status: StructuredResultStatus.Failed,
            Summary: null,
            Output: null,
            Data: null,
            Warnings: Array.Empty<string>(),
            Errors: new[] { message },
            Artifacts: Array.Empty<string>(),
            Metadata: null);

        run.StructuredResult = JsonSerializer.Serialize(structuredResult, StructuredAgentResult.JsonOptions);
        run.StructuredResultVersion = structuredResult.Version;
        run.TransitionTo(AgentRunStatus.Failed);

        // Same reasoning as the main success/failure path: the terminal
        // status must persist even if cancellationToken is already cancelled.
        await _agentRunRepository.UpdateAsync(run, CancellationToken.None);

        _logger.LogWarning("AgentRunFailed AgentRunId={AgentRunId} Reason={Reason}", run.Id, message);

        return new AgentOrchestrationResult(
            Executed: true,
            Success: false,
            FinalStatus: AgentRunStatus.Failed,
            Output: null,
            DurationMs: 0,
            ToolCallNames: Array.Empty<string>(),
            AvailableToolNames: Array.Empty<string>(),
            ErrorMessage: message,
            FailureCategory: category,
            CorrelationId: run.CorrelationId);
    }

    private static AgentOrchestrationResult NotExecuted(AgentRun? run, string message) => new(
        Executed: false,
        Success: false,
        FinalStatus: run?.Status ?? AgentRunStatus.Failed,
        Output: null,
        DurationMs: 0,
        ToolCallNames: Array.Empty<string>(),
        AvailableToolNames: Array.Empty<string>(),
        ErrorMessage: message,
        FailureCategory: null,
        CorrelationId: run?.CorrelationId ?? Guid.Empty);
}
