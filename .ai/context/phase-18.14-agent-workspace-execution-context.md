# Phase 18.14 — Deterministic Agent Workspace & Execution Context

## 1. Current Execution-Context Findings (empirically re-verified)

Re-confirmed, by reading the actual code (not assuming the Phase 18.13 finding still held):

- `HermesAgentRuntime.ExecuteAsync` (`apps/src/AI.Factory.Infrastructure/Agents/HermesAgentRuntime.cs`) built its `AgentExecutionRequest` with `WorkingDirectory: string.Empty`, unconditionally, for every AgentRun including WorkItem-backed ones.
- `HermesAgentExecutor.ExecuteAsync` (`apps/src/AI.Factory.Infrastructure/Agents/HermesAgentExecutor.cs`) never read `request.WorkingDirectory` at all — it always built a fresh `Guid.NewGuid()`-named scratch folder under `HermesOptions.WorkspaceRoot` and bind-mounted *that* into Docker as `/workspace`.
- `FileSystemArtifactRecorder.RecordWorkspaceArtifactsAsync` only indexed whatever files existed in that scratch folder as `Artifact` rows — it never copied anything into or out of `Project.RootPath`.
- Net effect: a real (non-fake) agent execution had zero visibility into the actual Project source, and `VerificationGate` (which builds/tests `Project.RootPath`) verified a directory the agent never touched.
- `TaskSchedulerOptions.MaxConcurrentTasks` defaults to `1`, and concurrency is scoped per-project (`TaskDispatchScheduler`) — no same-project concurrent-write race exists under default configuration.
- `Project.RootPath` is set in exactly one place in `apps/src`: `ProjectService.CreateAsync` (`RootPath = $"projects/{slug}"`). Confirmed by `grep -rn "RootPath ="` across `apps/src` — the only other hit is the original EF migration column definition. No API input, agent output, or planner output ever sets it.
- `DotNetBuildExecutor`/`DotNetTestExecutor` already pass `project.RootPath` as-is with no boundary check — unchanged by this phase, only matched by an equivalent-or-better check on the agent-execution path.
- **New finding, not previously documented**: `ProjectService.CreateAsync` never creates `projects/{slug}` on disk — it only sets the string field. No code anywhere in `apps/src` calls `Directory.CreateDirectory` for a Project's RootPath. This means a Project created through the real API today has a RootPath that does not exist as a real directory unless something external (an operator, a git checkout, a future phase) creates it. This phase does not fix that — see "Remaining Gaps" — but it does mean the new deterministic-failure policy (§11) will legitimately reject such Projects today, which is the correct, safe behavior, not a bug.

## 2. Old Workspace-Preparation Behavior & Responsibility Classification

The deleted legacy path (`AutonomousCodingOrchestrator` / `OrchestrationRunExecutionEngine.CopyDirectory`, retired in Phase 18.13) used to: create a fresh scratch folder, copy `Project.RootPath` into it (excluding `bin`, `obj`, `node_modules`, `.git`, `.vs`), execute there, then leave the copy in place for artifact indexing.

| Old behavior | Classification | Disposition this phase |
|---|---|---|
| Fresh directory creation per execution | **B — already provided differently** | The Factory no longer creates a *copy* directory; it mounts the live `Project.RootPath` directly (when resolved) via `IWorkspaceResolver`. Directory *existence* is now a precondition checked by the resolver, not something this phase creates. |
| Source checkout/copy (`CopyDirectory`) | **C — obsolete** | No longer needed: the agent is mounted directly into the live Project directory (`-v {ProjectRootPath}:/workspace`, read-write), so there is nothing to copy back either. |
| Artifact placement/indexing | **A — still required, now fixed** | `FileSystemArtifactRecorder` still indexes workspace files as `Artifact` rows, but now excludes `bin`/`obj`/`node_modules`/`.git`/`.vs` (same list as the deleted `CopyDirectory`) — required because `workspacePath` can now be an entire live Project directory instead of a small scratch folder. |
| Task materialization / environment setup | **N/A — never part of workspace prep** | Confirmed by reading the deleted code's history: these were WorkItem/prompt construction concerns already fully owned by `AgentOrchestrator`/`IAgentInstructionBuilder` in the current pipeline, untouched by this phase. |

## 3. Final Workspace Authority

`Project.RootPath`, resolved through `IWorkspaceResolver`, is the sole authority for a WorkItem-backed AgentRun's execution directory. No path from agent output, planner output, or LLM-generated content is ever consulted. A Planner-only AgentRun (`WorkItemId == null`) is explicitly out of scope (per the phase title/§22) and is unaffected — it still executes with `HermesAgentExecutor`'s own scratch folder, exactly as before.

## 4. `IWorkspaceResolver` Design

New Application-layer abstraction (`apps/src/AI.Factory.Application/Abstractions/IWorkspaceResolver.cs`):

```csharp
public interface IWorkspaceResolver
{
    WorkspaceResolution Resolve(Project project);
}

public sealed record WorkspaceResolution(bool IsValid, string? Path, string? FailureReason);
```

Sole implementation, `ProjectWorkspaceResolver` (`apps/src/AI.Factory.Application/Orchestration/ProjectWorkspaceResolver.cs`):

1. Rejects null/whitespace `RootPath`.
2. `Path.GetFullPath` to get an absolute path (catches malformed paths).
3. If `AgentWorkspaceOptions.AllowedRoot` is configured, reuses the existing `WorkspacePathGuard.EnsureWithinRoot` — no second path-traversal/symlink-escape algorithm was written.
4. Rejects a resolved path that does not exist as a directory.
5. Returns a `Valid`/`Invalid` result — never throws to the caller, never falls back to an implicit directory.

Deterministic and pure: the same `Project` always resolves to the same path, with no process-local caching or state — verified by a dedicated test (`Resolve_SameProject_ResolvesSamePath_Deterministically`) and by the cross-scope recovery test described in §9.

## 5. `AgentWorkspaceOptions.AllowedRoot`

New optional Application-layer options class. `AllowedRoot` defaults to `null`:

- **Unset (default)** — preserves today's implicit trust model for `Project.RootPath` (identical posture to `DotNetBuildExecutor`/`DotNetTestExecutor`, which apply no boundary check today): existence-checked only.
- **Configured** — every resolved Project workspace must stay inside `AllowedRoot`, enforced via `WorkspacePathGuard.EnsureWithinRoot`; a sibling directory, a parent directory, or a `../` traversal attempt is rejected.

No `appsettings.json` entry was added for `"AgentWorkspace"` in any environment — the class already defaults to the safe (unset) behavior, and no existing convention in this repo documents an intentionally-unset optional section with a placeholder JSON block (checked all six `appsettings*.json` files; every section present is fully specified, never a documented-but-empty placeholder).

## 6. AgentRuntime Integration

- `AgentRun.WorkspacePath` — new `[NotMapped]` property on the Domain entity (`apps/src/AI.Factory.Domain/Entities/AgentRun.cs`), following the exact precedent of the existing non-persisted execution-time `AgentRun` snapshot `AgentOrchestrator.BuildExecutionSnapshot` already constructs. EF Core excludes it from the model entirely — confirmed by `dotnet ef migrations has-pending-model-changes` reporting "No changes have been made to the model since the last migration."
- `AgentOrchestrator.ExecuteAsync` (`apps/src/AI.Factory.Application/Agents/AgentOrchestrator.cs`) now takes `IProjectRepository` and `IWorkspaceResolver` as constructor dependencies. After the existing `AgentDefinition is null` check and before tool/memory resolution: if `run.WorkItemId is not null`, it loads the `Project` by `run.ProjectId` (failing deterministically — `AgentRunStatus.Failed` / `FailureCategory.Validation` — if `ProjectId` is missing or the `Project` cannot be found) and resolves the workspace via `IWorkspaceResolver`. An invalid resolution fails the run the same way, **without ever calling `IAgentRuntime`**. A Planner-only run (`WorkItemId == null`) skips all of this — `workspacePath` stays `null`, execution proceeds exactly as before.
- `BuildExecutionSnapshot` now takes the resolved `workspacePath` and sets it on the constructed execution-time `AgentRun` snapshot passed to `IAgentRuntime`.
- `HermesAgentRuntime.ExecuteAsync` now builds `AgentExecutionRequest` with `WorkingDirectory: agentRun.WorkspacePath ?? string.Empty` (was a bare `string.Empty` literal).
- `HermesAgentExecutor.ExecuteAsync`: when `request.WorkingDirectory` is non-empty, it is used **directly** as the Docker bind-mount source (`-v {workspacePath}:/workspace`, the same, unchanged bind-mount mechanism) — with a defensive `Directory.Exists` re-check (the resolver validated this at resolution time, but the directory could vanish, or be stale across a Worker restart, before the container actually starts); a missing directory fails deterministically (audit-logged as `AuditSeverity.Critical`, `AgentExecutionResult(false, ..., WorkspacePath: null)`) and never silently falls back to scratch. When `WorkingDirectory` is empty (the one remaining legitimate case — a Planner-only AgentRun), the exact original scratch-folder behavior is preserved unchanged.

## 7. Path-Security Behavior

- Reuses `WorkspacePathGuard.EnsureWithinRoot` (unchanged) — no second traversal/symlink-escape algorithm.
- Docker bind mounts remain read-write (no `:ro` anywhere in `HermesAgentExecutor`) — an agent executing against a resolved Project workspace can modify real project files, which is required for a coding agent to do its job.
- `AgentWorkspaceOptions.AllowedRoot`, when configured, is the sole additional boundary; proven by `WorkspaceResolverTests` (sibling directory rejected, parent directory rejected, `../../` traversal rejected — all via the real `WorkspacePathGuard`, not a test double).

## 8. Manual API / Repair Behavior

`/api/orchestration-runs` (`OrchestrationRunExecutionEngine`, unchanged this phase) delegates to `ITaskDispatchScheduler.DispatchWorkItemAsync`, which creates a WorkItem-backed `AgentRun` (`ProjectId` + `WorkItemId` both set) that is executed by the *same* `AgentRunWorker` → `AgentOrchestrator.ExecuteAsync` as scheduler-dispatched and repair-dispatched (`TaskDispatchScheduler.DispatchRepairAsync`) runs — confirmed by reading `TaskDispatchScheduler.cs`: both `DispatchWorkItemAsync` and `DispatchRepairAsync` construct their `AgentRun` with `ProjectId = workItem.ProjectId, WorkItemId = workItem.Id`. Since workspace resolution lives inside `AgentOrchestrator.ExecuteAsync` itself, manual, scheduler, and repair dispatch all resolve identically **by construction** — there is no separate legacy workspace implementation for any of them. This is proven at the `AgentOrchestrator` level (`AgentOrchestratorTests`) rather than duplicated three times, per the phase's own guidance (the dispatch path was already unified in Phase 18.13).

## 9. Restart / Recovery Behavior

Workspace resolution depends only on `AgentRun.ProjectId` → `Project.RootPath`, re-derived fresh on every call — no process-local cache, no in-memory state. Proven by `ExecuteAsync_RecoveredAgentRun_ResolvesSameWorkspace_FromPersistedStateAlone`: two independent `AgentOrchestrator` instances, each against its own fresh `FactoryDbContext` (simulating separate Worker instances/restarts), resolve the exact same absolute path for the same Project. A resolved directory that vanishes between resolution and Hermes actually running is caught defensively by `HermesAgentExecutor`'s `Directory.Exists` re-check (§6) and fails deterministically rather than silently falling back.

## 10. Container / Host Assumptions

Confirmed by reading `SystemProcessRunner.cs` and `IProcessRunner.cs`: the scratch-folder path (today's existing, working case) is a plain host filesystem path, passed straight into `ProcessStartInfo.WorkingDirectory` and into the `docker run -v host:container` argument built by `HermesAgentExecutor`. `Project.RootPath`, resolved via the identical `Path.GetFullPath`-based mechanism, is also a plain host filesystem path on the same host. No new host/container path-translation layer was introduced because none is required — this reuses the exact bind-mount call that already worked for the scratch-folder case; only the *source* of the path changed (Factory-resolved Project directory vs. a freshly generated Guid folder), not the mechanism. This reasoning is explicitly confirmed, not assumed.

## 11. Workspace Preparation Decisions

No workspace copy, checkout, or provisioning service was added — directory creation/checkout is classified Obsolete/Already-provided-differently (§2) and explicitly out of this phase's scope (§17 "No Scope Creep": no new repository/checkout service unless proven required, and it wasn't — the design mounts the live directory instead). The only new filesystem-touching behavior this phase adds is the defensive existence re-check in `HermesAgentExecutor` and the exclusion filter in `FileSystemArtifactRecorder`.

## 12. Schema Result

Zero migration. `AgentRun.WorkspacePath` is `[NotMapped]`; `dotnet ef migrations has-pending-model-changes --startup-project ../AI.Factory.Api --project .` (run from `apps/src/AI.Factory.Infrastructure`) reports **"No changes have been made to the model since the last migration."**

## 13. Focused Test Results

New/updated test files (all passing):

- `apps/tests/AI.Factory.Application.Tests/WorkspaceResolverTests.cs` (new) — valid path, null/empty/whitespace RootPath, nonexistent directory, `AllowedRoot` unset vs. configured (inside/sibling/parent/traversal), deterministic repeat resolution.
- `apps/tests/AI.Factory.Application.Tests/AgentOrchestratorTests.cs` (extended) — WorkItem-backed run with valid Project resolves workspace and the fake runtime receives it; invalid/nonexistent workspace fails deterministically without invoking the runtime; missing `ProjectId` on a WorkItem-backed run fails deterministically without invoking the runtime; Planner-only run is unaffected (workspace stays null, runtime still called); two independent orchestrator instances against the same Project resolve identically (recovery semantics).
- `apps/tests/AI.Factory.Application.Tests/HermesAgentExecutorTests.cs` (extended; pre-existing tests' placeholder `"/ignored"` WorkingDirectory literal changed to `string.Empty` since the field is no longer inert) — a non-empty `WorkingDirectory` is used directly as the bind-mount source instead of a generated scratch path; an empty `WorkingDirectory` still produces the original fresh-scratch-folder behavior; a nonexistent `WorkingDirectory` fails deterministically without falling back to scratch (and without ever invoking the process runner).
- `apps/tests/AI.Factory.Application.Tests/FileSystemArtifactRecorderTests.cs` (new) — files under `bin`/`obj`/`node_modules`/`.git`/`.vs` are never recorded as `Artifact` rows; an ordinary source file still is.
- Four pre-existing integration test files (`AgentRunFullLifecycleIntegrationTests.cs`, `WorkItemExecutionIntegrationTests.cs`, `RepairVerificationLoopTests.cs`, `ProjectCompletionIntegrationTests.cs`) construct `AgentOrchestrator` directly and needed their constructor call sites updated for the two new dependencies. Three of them (`WorkItemExecutionIntegrationTests`, `RepairVerificationLoopTests`, `ProjectCompletionIntegrationTests`) drive WorkItem-backed AgentRuns through the real orchestrator and previously seeded Projects with the default, non-existent `"projects/{slug}"` RootPath (one file's own comment explicitly said so: *"not an actual dotnet build against a seeded Project's (non-existent) RootPath"*) — this is a direct, necessary consequence of this phase's change (identical in kind to the `FileSystemArtifactRecorder` exclusion-list fix the spec itself anticipated), so each now creates a real temporary directory for the seeded Project's RootPath, cleaned up in `Dispose()`. `ProjectPlannerServiceTests.cs` needed only the constructor-argument fix — every AgentRun it executes through the orchestrator is Planner-only (`WorkItemId == null`), so workspace resolution never triggers there.

Ran, individually, before the full suite: `dotnet test --filter "FullyQualifiedName~WorkspaceResolverTests|FullyQualifiedName~FileSystemArtifactRecorderTests|FullyQualifiedName~HermesAgentExecutorTests|FullyQualifiedName~AgentOrchestratorTests|FullyQualifiedName~HermesAgentRuntimeTests"` → **54 passed, 0 failed.**

## 14. Full Build/Test Results

```
dotnet build apps/AI.Factory.slnx -c Debug     → Build succeeded. 0 Warning(s), 0 Error(s).
dotnet build apps/AI.Factory.slnx -c Release   → Build succeeded. 0 Warning(s), 0 Error(s).
dotnet test  apps/AI.Factory.slnx -c Debug --no-build
    AI.Factory.Worker.Tests.dll      : 84  passed, 0 failed, 0 skipped
    AI.Factory.Application.Tests.dll : 727 passed, 0 failed, 0 skipped
    AI.Factory.Api.Tests.dll         : 293 passed, 0 failed, 0 skipped
    Total: 1104 passed, 0 failed
dotnet ef migrations has-pending-model-changes --startup-project ../AI.Factory.Api --project .
    → "No changes have been made to the model since the last migration."
```

Repository-wide `grep -rn "WorkingDirectory" apps/src apps/tests` confirms:

- `HermesAgentRuntime.cs` now passes `agentRun.WorkspacePath ?? string.Empty` — not a bare `string.Empty` literal.
- The only remaining places an empty/legitimate-fallback working directory occurs:
  - `HermesAgentRuntime` itself, when `agentRun.WorkspacePath` is `null` — exactly the one sanctioned case: a Planner-only AgentRun (`WorkItemId == null`), out of this phase's scope by design.
  - `HermesAgentExecutorTests.cs` tests that deliberately pass `string.Empty` to exercise/preserve that exact fallback path, plus the new tests that deliberately pass a real or a nonexistent path to exercise the new forwarding/failure behavior.
  - `SystemProcessRunner.cs` / `IProcessRunner.cs` — the general-purpose, unrelated process-runner contract (`WorkingDirectory` optional for any process, not agent-specific).
  - `DockerComposeValidationTests.cs` / `DockerComposeReleaseValidationTests.cs` — unrelated deployment-validation tests, not agent execution.

## 15. Real Hermes E2E

An opt-in mechanism already exists: `apps/tests/AI.Factory.Api.Tests/RealHermesApiE2ETests.cs`, gated by `AI_FACTORY_HERMES_SMOKE_TEST=true` (Docker + a reachable OpenRouter/OmniRoute-compatible provider required). It currently creates an AgentRun with no `WorkItemId`/`ProjectId` (Planner-shaped), so it does not exercise this phase's new workspace-mount path at all.

**Not extended, and not run.** This sandboxed execution environment has no Docker daemon and no network access to an LLM provider, so any new real-Hermes assertion could not actually be executed or verified here — writing one anyway would mean shipping test code whose correctness is unverified, which conflicts with "never claim tested if not tested" (AGENTS.md) and the phase's own "never fabricate the result" instruction. Extending the harness properly (creating a real Project with a real RootPath + marker file, dispatching a WorkItem through `TaskDispatchScheduler`, then asserting the container observed it) is also more than a one-line change to the existing smoke test — it is new test infrastructure, which the spec explicitly permits skipping ("only if that mechanism is already present and usable without new infrastructure"). This is called out below as the clearest concrete next step for whoever has a Docker-and-network-enabled environment to run it in.

## 16. Remaining Gaps / Risks

1. **`ProjectService.CreateAsync` never provisions `RootPath` on disk** (§1). Every WorkItem-backed AgentRun for a Project created through the real API today will fail workspace resolution deterministically (safely — not silently) until something (an operator, a git-checkout step, a future phase) actually creates that directory. This is a pre-existing gap this phase surfaces and fails safely on; provisioning/checkout is explicitly out of this phase's scope (§17 "No Scope Creep" — no new repository/checkout service unless proven required).
2. **`TaskSchedulerOptions.MaxConcurrentTasks > 1` for a single project** would now cause real concurrent-write races against the live Project directory (previously, concurrent scratch folders were isolated from each other and from the real source). This is a known, pre-existing, out-of-scope risk (default is `1`, unchanged by this phase) that becomes slightly more consequential with this change — flagged explicitly, not newly introduced.
3. Real-Hermes E2E coverage of the new workspace-mount path is not yet written (§15) — next concrete step for an environment with Docker + provider access.

## 17. Definition-of-Done / NO-GO Check (§20)

None of the NO-GO conditions hold:

- Agent execution cannot silently use an implicit current directory for a WorkItem-backed run — an invalid/missing workspace fails the AgentRun deterministically before `IAgentRuntime` is ever called.
- The workspace is never supplied by the LLM/agent/planner — only `Project.RootPath` via `IWorkspaceResolver`.
- Manual, scheduler, and repair execution all resolve through the same `AgentOrchestrator.ExecuteAsync` → `IWorkspaceResolver` call (§8) — no separate implementation exists for any of them.
- Repair execution uses the identical resolution path — same reasoning.
- An invalid `Project.RootPath` never falls back to an unsafe directory — it fails deterministically.
- Path traversal cannot escape the configured `AllowedRoot` (when configured) — proven by dedicated tests reusing `WorkspacePathGuard`.
- No second/legacy execution engine was created — `AgentOrchestrator`/`HermesAgentRuntime`/`HermesAgentExecutor` were extended in place.
- Workspace state depends on no process-local memory — proven by the cross-instance recovery test (§9).
- No real-execution path reaches Hermes with an empty `WorkingDirectory` for a WorkItem-backed run — the only remaining empty-`WorkingDirectory` case is the explicitly out-of-scope Planner-only run.

```
Agent Workspace & Execution Context: VERIFIED
Final Decision: GO
```
