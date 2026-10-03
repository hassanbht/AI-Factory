using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Agents;

/// <summary>
/// The one Factory-owned implementation of <see cref="IAgentOrchestrator"/>.
/// Coordinates a single AgentRun end to end: state-machine eligibility check,
/// AgentDefinition load, tool/memory resolution, a single <see cref="IAgentRuntime"/>
/// call, Factory-side workspace artifact recording, then the resulting status
/// transition — all through existing abstractions. It is not itself a runtime:
/// it never touches Docker, processes, or Hermes, and it never bypasses
/// AgentRun.TransitionTo's centralized validation.
/// </summary>
public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IAgentRunRepository _agentRunRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly IAgentMemory _memory;
    private readonly IAgentRuntime _runtime;
    private readonly IWorkspaceResolver _workspaceResolver;
    private readonly IAgentInstructionBuilder _instructionBuilder;
    private readonly IStructuredAgentResultParser _structuredResultParser;
    private readonly IModelRouter? _modelRouter;
    private readonly ModelRoutingOptions? _routingOptions;
    private readonly IArtifactRecorder? _artifactRecorder;
    private readonly IArtifactRepository? _artifactRepository;
    private readonly ExecutionLimitsOptions? _executionLimits;
    private readonly AgentWorkerOptions? _workerOptions;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        IAgentRunRepository agentRunRepository,
        IProjectRepository projectRepository,
        IAgentToolRegistry toolRegistry,
        IAgentMemory memory,
        IAgentRuntime runtime,
        IWorkspaceResolver workspaceResolver,
        IAgentInstructionBuilder instructionBuilder,
        IStructuredAgentResultParser structuredResultParser,
        ILogger<AgentOrchestrator> logger,
        IModelRouter? modelRouter = null,
        ModelRoutingOptions? routingOptions = null,
        IArtifactRecorder? artifactRecorder = null,
        IArtifactRepository? artifactRepository = null,
        ExecutionLimitsOptions? executionLimits = null,
        AgentWorkerOptions? workerOptions = null)
    {
        if ((artifactRecorder is null) != (artifactRepository is null))
        {
            throw new ArgumentException(
                "Artifact recording needs both an IArtifactRecorder and an IArtifactRepository, or neither.",
                artifactRecorder is null ? nameof(artifactRecorder) : nameof(artifactRepository));
        }

        _agentRunRepository = agentRunRepository;
        _projectRepository = projectRepository;
        _toolRegistry = toolRegistry;
        _memory = memory;
        _runtime = runtime;
        _workspaceResolver = workspaceResolver;
        _instructionBuilder = instructionBuilder;
        _structuredResultParser = structuredResultParser;
        _modelRouter = modelRouter;
        _routingOptions = routingOptions;
        _artifactRecorder = artifactRecorder;
        _artifactRepository = artifactRepository;
        _executionLimits = executionLimits;
        _workerOptions = workerOptions;
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

        // Phase 18.14 — only WorkItem-backed AgentRuns need a resolved
        // workspace; a Planner-only AgentRun (WorkItemId == null) is
        // explicitly out of this phase's scope and keeps its exact prior
        // behavior (workspacePath stays null, runtime still gets called).
        string? workspacePath = null;

        if (run.WorkItemId is not null)
        {
            if (run.ProjectId is not { } projectId)
            {
                return await FailRunningAsync(
                    run, "AgentRun is WorkItem-backed but has no ProjectId; cannot resolve a workspace.",
                    FailureCategory.Validation, cancellationToken);
            }

            var project = await _projectRepository.GetByIdAsync(projectId, cancellationToken);

            if (project is null)
            {
                return await FailRunningAsync(
                    run, $"Project '{projectId}' was not found; cannot resolve a workspace.",
                    FailureCategory.Validation, cancellationToken);
            }

            var resolution = _workspaceResolver.Resolve(project);

            if (!resolution.IsValid)
            {
                _logger.LogWarning(
                    "AgentWorkspaceResolutionFailed AgentRunId={AgentRunId} ProjectId={ProjectId} Reason={Reason}",
                    run.Id, projectId, resolution.FailureReason);

                return await FailRunningAsync(
                    run, resolution.FailureReason ?? "Workspace could not be resolved.",
                    FailureCategory.Validation, cancellationToken);
            }

            workspacePath = resolution.Path;
        }

        var availableToolNames = _toolRegistry.GetDescriptors().Select(x => x.Name).ToArray();

        // Phase 18.26 §13 — resolve the role's candidates BEFORE execution and
        // persist the selected model/provider on the AgentRun itself. Until
        // now the resolved model only ever reached the runtime via a transient
        // snapshot, so AgentRun.Model stayed null in the database and no
        // post-run analysis could say which model actually ran. An explicit
        // AgentRun.Model still wins and disables fallback entirely: a caller
        // that named a model chose it deliberately.
        var explicitModel = !string.IsNullOrWhiteSpace(run.Model);
        var candidates = explicitModel ? Array.Empty<ModelSelection>() : ResolveRoleCandidates(run);
        var category = run.AgentDefinition?.AgentType is { } at && _routingOptions?.AgentTypeCategories.TryGetValue(at, out var cat) == true ? cat : null;
        string? primaryModel = null;

        if (!explicitModel)
        {
            if (candidates.Count > 0)
            {
                run.Model = candidates[0].ModelId;
                run.Provider ??= candidates[0].Provider;
                primaryModel = candidates[0].ModelId;
                await _agentRunRepository.UpdateAsync(run, cancellationToken);
            }
            else if (_modelRouter is not null && _routingOptions is not null)
            {
                var roleName = run.AgentDefinition?.AgentType ?? "(unspecified)";
                _logger.LogWarning(
                    "AgentOrchestratorRejected AgentRunId={AgentRunId} AgentType={AgentType} Reason=NoModelRouteResolved",
                    run.Id, roleName);

                return await FailRunningAsync(
                    run,
                    $"No explicit model or routing decision resolved for AgentType '{roleName}'. Silent DefaultModel fallback is forbidden.",
                    FailureCategory.Validation,
                    cancellationToken);
            }
        }
        else
        {
            primaryModel = run.Model;
        }

        if (!run.ExecutionTimeout.HasValue)
        {
            TimeSpan effectiveTimeout;
            if (_workerOptions is not null && _workerOptions.ExecutionTimeout > TimeSpan.Zero)
            {
                var safetyMargin = TimeSpan.FromSeconds(Math.Min(15, Math.Max(1, _workerOptions.ExecutionTimeout.TotalSeconds * 0.05)));
                effectiveTimeout = _workerOptions.ExecutionTimeout > safetyMargin
                    ? _workerOptions.ExecutionTimeout - safetyMargin
                    : _workerOptions.ExecutionTimeout;
            }
            else if (_executionLimits is not null && _executionLimits.AgentExecutionTimeoutMinutes > 0)
            {
                effectiveTimeout = TimeSpan.FromMinutes(_executionLimits.AgentExecutionTimeoutMinutes);
            }
            else
            {
                effectiveTimeout = TimeSpan.FromMinutes(5);
            }

            run.ExecutionTimeout = effectiveTimeout;
            run.ExecutionDeadline = DateTimeOffset.UtcNow + effectiveTimeout;
        }

        var memoryEntries = await SearchMemorySafelyAsync(run, cancellationToken);
        var executionRun = BuildExecutionSnapshot(run, availableToolNames, memoryEntries, workspacePath);

        var runtimeResult = await _runtime.ExecuteAsync(executionRun, cancellationToken);

        // Phase 18.26 §10 — controlled fallback. Permitted ONLY for
        // infrastructure/provider/model-availability failures, and only to the
        // role's next configured candidate. A failed test, a failed acceptance
        // contract, invalid Planner output or a genuine coding failure is a
        // result, not an outage: re-running those on another model would be
        // using fallback to bypass Factory validation, which §10 forbids.
        var fallbackUsed = false;
        string? fallbackReason = null;

        if (!runtimeResult.Success
            && candidates.Count > 1
            && IsFallbackEligible(runtimeResult.FailureCategory))
        {
            var fallback = candidates[1];
            fallbackReason = runtimeResult.FailureCategory?.ToString();

            _logger.LogWarning(
                "AgentRunModelFallback AgentRunId={AgentRunId} PrimaryModel={PrimaryModel} FallbackModel={FallbackModel} Reason={FailureCategory}",
                run.Id, run.Model, fallback.ModelId, runtimeResult.FailureCategory);

            run.Attempt++;
            run.Model = fallback.ModelId;
            run.Provider = fallback.Provider;
            await _agentRunRepository.UpdateAsync(run, CancellationToken.None);

            executionRun = BuildExecutionSnapshot(run, availableToolNames, memoryEntries, workspacePath);
            runtimeResult = await _runtime.ExecuteAsync(executionRun, cancellationToken);
            fallbackUsed = true;
        }

        // Execution-adapter Phase C — workspace artifacts are recorded here, by the
        // Factory, after the (final) execution has returned, not inside the
        // runtime. Same recorder, same condition (successful, WorkItem-backed run
        // with a resolved workspace), same token and same references as before;
        // a fallback is only ever attempted after a failure, so at most one
        // execution per AgentRun records artifacts.
        runtimeResult = await RecordWorkspaceArtifactsAsync(run, workspacePath, runtimeResult, cancellationToken);

        _logger.LogInformation(
            "AgentRunModelTelemetry AgentRunId={AgentRunId} AgentType={AgentType} Model={Model} Provider={Provider} "
                + "FallbackUsed={FallbackUsed} Attempt={Attempt} DurationMs={DurationMs} Success={Success}",
            run.Id, run.AgentDefinition?.AgentType, run.Model, run.Provider,
            fallbackUsed, run.Attempt, runtimeResult.DurationMs, runtimeResult.Success);

        var finalStatus = runtimeResult.Success
            ? AgentRunStatus.Succeeded
            : MapFailureStatus(runtimeResult.FailureCategory);

        var structuredResult = BuildStructuredResult(runtimeResult, finalStatus, run, category, primaryModel, fallbackUsed, fallbackReason);

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
        try
        {
            await _agentRunRepository.UpdateAsync(run, CancellationToken.None);
        }
        catch (ConcurrencyConflictException)
        {
            // Phase M — another writer (cancellation, orphan recovery) already decided this run's lifecycle;
            // the repository refuses to overwrite it. That writer owns the outcome and the WorkItem sync.
            _logger.LogWarning(
                "AgentRunLostLifecycleRace AgentRunId={AgentRunId} AttemptedStatus={Status}", run.Id, finalStatus);
            return NotExecuted(run, "AgentRun lifecycle was decided by another writer (cancelled or recovered) during execution.");
        }

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

    /// <summary>
    /// Records every file in the Factory-resolved workspace as an Artifact (the
    /// existing IArtifactRecorder semantics) and returns the result carrying the
    /// WorkItem's artifact ids for this Project. Only for a successful run that
    /// has both a ProjectId and a WorkItemId and a resolved workspace; every other
    /// run returns the runtime result unchanged. Recorder/repository exceptions,
    /// including cancellation, propagate exactly as they did from the runtime.
    /// </summary>
    private async Task<AgentRunExecutionResult> RecordWorkspaceArtifactsAsync(
        AgentRun run,
        string? workspacePath,
        AgentRunExecutionResult runtimeResult,
        CancellationToken cancellationToken)
    {
        if (!runtimeResult.Success
            || _artifactRecorder is null
            || _artifactRepository is null
            || run.ProjectId is not { } projectId
            || run.WorkItemId is not { } workItemId
            || string.IsNullOrWhiteSpace(workspacePath))
        {
            return runtimeResult;
        }

        await _artifactRecorder.RecordWorkspaceArtifactsAsync(projectId, workItemId, workspacePath, cancellationToken);

        var artifacts = await _artifactRepository.GetByWorkItemIdAsync(workItemId, cancellationToken);

        return runtimeResult with
        {
            ArtifactReferences = artifacts
                .Where(a => a.ProjectId == projectId)
                .Select(a => a.Id.ToString())
                .ToArray()
        };
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
        AgentRun run,
        IReadOnlyList<string> availableToolNames,
        IReadOnlyList<AgentMemoryEntry> memoryEntries,
        string? workspacePath)
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
            // Resolved and persisted by ExecuteAsync before this point.
            Model = run.Model,
            Input = instructionText,
            WorkspacePath = workspacePath,
            ExecutionTimeout = run.ExecutionTimeout,
            ExecutionDeadline = run.ExecutionDeadline,
            CreatedAtUtc = run.CreatedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc,
            Version = run.Version
        };
    }

    /// <summary>
    /// Phase 18.21 §13/§14 — picks the model for this run's ROLE when the run
    /// does not name one itself. An explicit AgentRun.Model always wins; this
    /// only fills the gap that made Implementation and Repair
    /// indistinguishable, since nothing in the production pipeline ever set
    /// AgentRun.Model and both silently used the container's internal default.
    /// Routing stays entirely in configuration (AgentType -> category ->
    /// candidate models), so no model id is hard-coded here. A missing or
    /// unusable profile is not an error: the run simply carries no model and
    /// HermesOptions.DefaultModel remains the last-resort fallback.
    /// </summary>
    /// <summary>
    /// Phase 18.26 §10/§18 — the exact, closed set of failure classes a model
    /// fallback may answer. Every one of them means "this request never got a
    /// fair evaluation": the provider, the router or the transport failed.
    /// Everything else — Validation, Build, Test, Agent, Review, Approval,
    /// Timeout, Cancelled — is a real result the Factory must keep, because
    /// re-running it on a second model would launder a failed acceptance
    /// contract or a failed test into a retry.
    /// </summary>
    private static bool IsFallbackEligible(FailureCategory? category) =>
        category is FailureCategory.Provider or FailureCategory.Model or FailureCategory.Infrastructure;

    private IReadOnlyList<ModelSelection> ResolveRoleCandidates(AgentRun run)
    {
        const int estimatedContextTokens = 16_000;

        var agentType = run.AgentDefinition?.AgentType;

        if (_modelRouter is null || _routingOptions is null || string.IsNullOrWhiteSpace(agentType))
        {
            return Array.Empty<ModelSelection>();
        }

        if (!_routingOptions.AgentTypeCategories.TryGetValue(agentType, out var category))
        {
            return Array.Empty<ModelSelection>();
        }

        try
        {
            var selections = _modelRouter.SelectCandidates(new ModelRoutingRequest(
                category, TaskComplexity.Standard, estimatedContextTokens, CostPreference.Balanced));

            _logger.LogInformation(
                "AgentRunModelResolved AgentRunId={AgentRunId} AgentType={AgentType} Category={Category} "
                    + "PrimaryModel={ModelId} FallbackCount={FallbackCount}",
                run.Id, agentType, category, selections[0].ModelId, selections.Count - 1);

            return selections;
        }
        catch (NoModelAvailableException ex)
        {
            _logger.LogWarning(
                "AgentRunModelUnresolved AgentRunId={AgentRunId} AgentType={AgentType} Category={Category} Reason={Reason}",
                run.Id, agentType, category, ex.Message);
            return Array.Empty<ModelSelection>();
        }
    }

    /// <summary>
    /// Parses the runtime's raw output into a structured result on success;
    /// on failure/cancellation/timeout builds a deterministic structured
    /// result directly from the already-known outcome rather than attempting
    /// to parse LLM text that was never produced (or is irrelevant).
    /// </summary>
    private StructuredAgentResult BuildStructuredResult(
        AgentRunExecutionResult runtimeResult,
        AgentRunStatus finalStatus,
        AgentRun run,
        string? category,
        string? primaryModel,
        bool fallbackUsed,
        string? fallbackReason)
    {
        StructuredAgentResult baseResult;

        if (!runtimeResult.Success)
        {
            baseResult = new StructuredAgentResult(
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
        else
        {
            var parseOutcome = _structuredResultParser.Parse(runtimeResult.Output);

            if (parseOutcome.IsValid)
            {
                baseResult = parseOutcome.Result!;
            }
            else
            {
                // The runtime call itself succeeded (real completion, real exit code
                // 0) but the agent's own output did not honor the structured-output
                // contract — never silently accepted (Phase 18.1.8 §4). AgentRun's
                // own Status is untouched by this; it stays whatever the runtime
                // reported.
                _logger.LogWarning(
                    "StructuredAgentResultInvalid Reason={Reason} Message={Message}",
                    parseOutcome.FailureReason, parseOutcome.Message);

                baseResult = new StructuredAgentResult(
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
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (baseResult.Metadata is not null)
        {
            foreach (var kvp in baseResult.Metadata)
            {
                metadata[kvp.Key] = kvp.Value;
            }
        }

        if (run.AgentDefinition?.AgentType is { } agentType)
        {
            metadata["AgentType"] = agentType;
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            metadata["Category"] = category;
        }

        if (!string.IsNullOrWhiteSpace(primaryModel))
        {
            metadata["PrimaryModel"] = primaryModel;
        }

        if (!string.IsNullOrWhiteSpace(run.Model))
        {
            metadata["SelectedModel"] = run.Model;
        }

        if (!string.IsNullOrWhiteSpace(run.Provider))
        {
            metadata["Provider"] = run.Provider;
        }

        metadata["FallbackUsed"] = fallbackUsed ? "true" : "false";
        if (fallbackReason is not null)
        {
            metadata["FallbackReason"] = fallbackReason;
        }
        metadata["Attempt"] = run.Attempt.ToString();

        // Execution-adapter Phase C — internal metadata only (no API/DTO surface).
        if (!string.IsNullOrWhiteSpace(runtimeResult.ExecutionAdapter))
        {
            metadata["ExecutionAdapter"] = runtimeResult.ExecutionAdapter;
        }

        if (!string.IsNullOrWhiteSpace(runtimeResult.ExitReason))
        {
            metadata["ExitReason"] = runtimeResult.ExitReason;
        }

        // Phase J — routing observability. "Provider" above is the model router's provider
        // id; "Gateway" is the model gateway the run was routed through (ModelRoute.GatewayKey
        // as the adapter received it). Only recorded when an adapter ran.
        if (!string.IsNullOrWhiteSpace(runtimeResult.ExecutionAdapter)
            && !string.IsNullOrWhiteSpace(runtimeResult.Provider))
        {
            metadata["Gateway"] = runtimeResult.Provider;
        }

        if (!string.IsNullOrWhiteSpace(runtimeResult.GatewayFailure))
        {
            metadata["GatewayFailure"] = runtimeResult.GatewayFailure;
        }

        AddRuntimeReport(metadata, runtimeResult.RuntimeReport);

        return baseResult with { Metadata = metadata };
    }

    /// <summary>
    /// Phase M — persists what the runtime itself reported (session, cost, request id, version,
    /// turns, timestamps) as AgentRun structured-result metadata. A field the runtime did not expose
    /// is simply absent — absence means NOT_AVAILABLE, never zero. Token counts keep their own
    /// AgentRun columns.
    /// </summary>
    private static void AddRuntimeReport(Dictionary<string, string> metadata, Abstractions.Execution.ExecutionRuntimeReport? report)
    {
        if (report is null)
        {
            return;
        }

        void Put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value;
            }
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        Put("RuntimeVersion", report.RuntimeVersion);
        Put("RuntimeSessionId", report.SessionId);
        Put("RuntimeStartedAtUtc", report.StartedAt?.UtcDateTime.ToString("O", invariant));
        Put("RuntimeCompletedAtUtc", report.CompletedAt?.UtcDateTime.ToString("O", invariant));
        Put("RuntimeExitCode", report.ExitCode?.ToString(invariant));
        Put("CostUsd", report.CostUsd?.ToString(invariant));
        Put("CostSource", report.CostUsd is null ? null : report.CostSource);
        Put("UpstreamRequestId", report.UpstreamRequestId);
        Put("RuntimeTurns", report.Turns?.ToString(invariant));
        Put("RuntimeReportedModel", report.ReportedModel);
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
