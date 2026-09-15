# Phase 18.15 — Project Workspace Provisioning & Repository Initialization

## 1. Current Provisioning Gap (empirically re-verified)

Re-confirmed by reading the actual code, matching the Phase 18.14 report's finding exactly:

- `ProjectService.CreateAsync` (`apps/src/AI.Factory.Application/Projects/ProjectService.cs`) set `RootPath = $"projects/{slug}"` on the `Project` entity but never created that directory on disk. `grep -rn "RootPath ="` across `apps/src` confirmed exactly one hit outside the EF migration column definition — this line.
- `IWorkspaceResolver`/`ProjectWorkspaceResolver` (Phase 18.14) require `Directory.Exists` to succeed, so every real Project created through the API failed workspace resolution deterministically — correct-but-unusable behavior, by design (Phase 18.14 explicitly deferred provisioning).
- Net effect before this phase: `POST /api/projects` → `Project` persisted → WorkItem created → dispatched → `AgentRun` → `AgentOrchestrator.ExecuteAsync` → `IWorkspaceResolver.Resolve` → **always Invalid** (`does not exist or is not accessible`) → `AgentRunStatus.Failed`, never reaching Hermes.

## 2. Filesystem Dependency Analysis

`grep -rn "Directory\.CreateDirectory|RootPath ="` across `apps/src` (post-change) shows exactly:

| Hit | Purpose |
|---|---|
| `ProjectService.cs:72` — `RootPath = $"projects/{slug}"` | The one and only place a Project's RootPath is ever set. |
| `ProjectWorkspaceProvisioner.cs:51` — `Directory.CreateDirectory(path!)` | The one and only place a Project workspace directory is ever created. |
| `DatabaseBackupService.cs`, `WorkerHealthCoordinator.cs` | Unrelated (backup directory, worker health-check directory). |
| `HermesAgentExecutor.cs:95` | Unrelated, pre-existing Phase 18.14 case: the Planner-only (`WorkItemId == null`) fresh scratch-folder fallback, used only when `AgentExecutionRequest.WorkingDirectory` is empty — never a second authority for `Project.RootPath`. |

No alternative Project-workspace-creation path exists. Spec §20/§21's "second workspace authority" NO-GO condition does not hold.

## 3. Provisioner Abstraction

Added `IProjectWorkspaceProvisioner` (`apps/src/AI.Factory.Application/Abstractions/IProjectWorkspaceProvisioner.cs`):

```csharp
public interface IProjectWorkspaceProvisioner
{
    ProvisioningResult Provision(Project project);
}

public sealed record ProvisioningResult(bool Success, string? Path, string? FailureReason);
```

Sole implementation, `ProjectWorkspaceProvisioner` (`apps/src/AI.Factory.Application/Orchestration/ProjectWorkspaceProvisioner.cs`). Single responsibility: create the directory, nothing else — no Git, no build/test, no Project lifecycle transitions.

**Shared resolve/provision logic (spec §10)**: the non-existence-checking half of `ProjectWorkspaceResolver.Resolve` (null/empty check + `Path.GetFullPath` + optional `AllowedRoot` boundary via the existing `WorkspacePathGuard`) was extracted **verbatim** into a new internal helper, `WorkspacePathResolution.ResolveBoundedPath(project, options)`, in the same `AI.Factory.Application.Orchestration` namespace (`ProjectWorkspaceResolver.cs`). Both `ProjectWorkspaceResolver.Resolve` (adds only the `Directory.Exists` check) and `ProjectWorkspaceProvisioner.Provision` (uses it to know *where* to create the directory) call this one helper — no second path-validity algorithm was written.

**"Provisioner creates, Resolver validates"**: after `Directory.CreateDirectory` succeeds, `ProjectWorkspaceProvisioner.Provision` does **not** independently declare success — it calls the injected `IWorkspaceResolver.Resolve(project)` and returns success only if the resolver itself agrees. This is the literal mechanism, not just a comment, that guarantees the provisioner never decides workspace validity differently than the component Agent execution actually uses.

`Directory.CreateDirectory` is itself idempotent (no-op success if the directory already exists), which naturally satisfies "directory absent → create, directory already exists → success" without special-casing. A file at the target path is explicitly detected via `File.Exists` and rejected before attempting creation.

## 4. Project Creation Integration

`ProjectService.CreateAsync` now provisions **before** persisting:

```text
validate name -> generate slug -> check duplicate -> build Project (RootPath set)
    -> _workspaceProvisioner.Provision(project)
        -> failure: throw IOException, no repository call is ever made
        -> success: _repository.AddAsync + SaveChangesAsync
```

**Before-persistence, not after-with-compensation**: chosen because it makes "no Project row exists whose workspace could not be provisioned" true by construction, with zero DB/filesystem compensation logic needed — there is nothing to roll back, since nothing was ever written. This directly satisfies spec §3/§6 without pretending the filesystem operation participates in the EF Core transaction.

`ProjectService`'s constructor gained an optional `IProjectWorkspaceProvisioner? workspaceProvisioner = null` parameter (mirroring the existing `IAuditLogger? auditLogger = null` null-object precedent). ~20 existing test call sites across the repo construct `new ProjectService(repo)` with a single argument; none pass a second positional argument (verified by `grep -rn "new ProjectService("`), so this is fully backward compatible. When omitted, a real (not no-op) provisioner is constructed internally with default `AgentWorkspaceOptions()` — deliberately, not a silent no-op, so the correctness guarantee ("no successful Project creation without a provisioned workspace") holds even for direct, non-DI construction, matching the actual production DI wiring's behavior rather than diverging from it for tests.

An optional `ProjectService.EnsureWorkspaceAsync(Guid projectId, CancellationToken)` was added per spec §17 — loads an existing Project and re-runs `Provision`, for future operational repair of historical Projects. It is wired as a normal service method only; **no new API endpoint or startup hook calls it**, confirmed by grep (no reference to `EnsureWorkspaceAsync` anywhere outside `ProjectService.cs` itself).

## 5. Path-Security Behavior

- Reuses `WorkspacePathGuard.EnsureWithinRoot` (unchanged) via the shared `WorkspacePathResolution` helper — no second traversal/symlink-escape algorithm.
- `AgentWorkspaceOptions.AllowedRoot`, when configured, is honored identically by both the provisioner and the resolver (same helper, same options instance in production DI).
- API input never reaches the provisioned path — `Provision` only ever receives the already-constructed `Project` entity; `RootPath` is generated exclusively from the Factory-owned slug, never from a request field.

**Discovered limitation (not a regression, not a NO-GO condition)**: `Project.RootPath`'s default value (`$"projects/{slug}"`) is a *relative* path. The (unchanged, copied-verbatim-per-spec) resolver logic calls `Path.GetFullPath(project.RootPath)` **before** applying any `AllowedRoot` boundary check — so a relative `RootPath` is resolved against the **process's current working directory**, not combined against `AllowedRoot`. Consequently, if `AllowedRoot` is configured in an environment whose working directory is not itself inside `AllowedRoot`, every new Project's default relative `RootPath` will fail provisioning (loudly — `CreateAsync` throws, no Project row is ever persisted; this is correct, safe failure, not a silent gap, and no §21 NO-GO condition applies). This pre-dates Phase 18.15 — Phase 18.14's own `WorkspaceResolverTests.cs` only ever exercises `AllowedRoot` with already-absolute `RootPath` values, never the ProjectService default — and was simply never exercised until this phase made `ProjectService.CreateAsync` actually call `Resolve`/`Provision`. Per the phase-18.15 instructions, `ProjectWorkspaceResolver`'s resolution behavior was extracted verbatim and must not be changed in this phase; documented here as a remaining execution gap for a future phase (e.g., making `RootPath` generation `AllowedRoot`-aware, or having the resolver combine relative candidates against `AllowedRoot` the same way `WorkspacePathGuard.EnsureWithinRoot` already does for a relative candidate). Operationally: environments that configure `AllowedRoot` today must ensure the API/Worker process's working directory resolves inside it.

## 6. Failure Semantics

| Case | Behavior |
|---|---|
| Invalid/malformed RootPath | `ResolveBoundedPath` rejects; `Provision` returns `Failed`; `CreateAsync` throws `IOException`; no Project row persisted. |
| `AllowedRoot` violation | Same as above, via `WorkspacePathGuard.EnsureWithinRoot`. |
| Path exists as a file | `File.Exists` check rejects before any `Directory.CreateDirectory` attempt; deterministic `Failed`, no exception escapes `Provision`. |
| Permission denied / parent path unavailable / directory creation failure | `Directory.CreateDirectory` exceptions (`IOException`, `UnauthorizedAccessException`, `NotSupportedException`) are caught inside `Provision`, logged (`LogError`), and converted to a deterministic `ProvisioningResult.Failed` — never an unhandled exception from the provisioner itself. |
| Any provisioning failure during Project creation | `ProjectService.CreateAsync` throws `System.IO.IOException` **before** any repository call — no Project row is ever written, so there is nothing to compensate/roll back. |

**Exception type chosen**: `System.IO.IOException`, an existing BCL type already used in this codebase for infrastructure-category failures (`DatabaseBackupService` catches it; `ResilienceAgentExecutorDecorator` throws it to signal a transient agent-execution error). No new exception type was added. `IFailureClassifier.Classify` already maps `IOException` → `FailureCategory.Infrastructure`, `IsTransient: true` (`apps/src/AI.Factory.Application/Abstractions/IFailureClassifier.cs:28`), and `ApiExceptionMapper.Map`'s existing fallback rule (`_ when classification.IsTransient && (Category == Provider || Category == Infrastructure)`) already maps that combination to **HTTP 503 Service Unavailable** with `ShouldAudit: true` — the correct "the server could not provision required infrastructure" signal, distinct from a 4xx client-input validation error, using machinery the codebase already had.

## 7. Existing Project Compatibility

No startup hook, migration, or background sweep was added. `grep -rn "EnsureWorkspaceAsync"` shows it is referenced only inside `ProjectService.cs` (its own definition) — nothing calls it automatically. Historical Projects whose `RootPath` does not exist continue to fail deterministically at `IWorkspaceResolver.Resolve`, exactly as before this phase (Phase 18.14's designed behavior, unchanged).

## 8. Repository Initialization Decision

Not implemented, as instructed. The provisioned workspace is an empty directory — no fake source files, no `.git`, no build artifacts, no placeholder project. Per spec:

> Workspace provisioning creates a valid empty Project workspace; source checkout/clone is a separate capability.

Classified as a **Future Phase**.

## 9. AgentRun Integration

`AgentOrchestrator`/`IWorkspaceResolver` (Phase 18.14) are entirely unchanged in their public contract — `ProjectWorkspaceResolver.Resolve` still does exactly what it did before, now via the shared helper. The only change in observable behavior: a freshly created Project's `RootPath` now **exists** on disk by the time any WorkItem for it is dispatched, so `Resolve` now legitimately returns `Valid` instead of always `Invalid`. Proven end-to-end by a new integration test (`WorkItemExecutionIntegrationTests.ProvisionedProject_WorkItemDispatch_AgentOrchestratorResolvesWorkspace`): a Project created through the real `ProjectService` (no manual `RootPath`/directory setup, unlike every pre-existing test in that file) is dispatched, and `AgentOrchestrator.ExecuteAsync` succeeds against a `FakeAgentRuntime`.

## 10. Verification Compatibility

`VerificationGate` (`apps/src/AI.Factory.Application/Orchestration/VerificationGate.cs`) was not touched — it still builds `BuildRequest`/`TestRequest` from `project.RootPath` directly, never through `IWorkspaceResolver`. The new integration test asserts `Path.GetFullPath(project.RootPath)` (what `dotnet build`/`dotnet test` resolve a relative `ProjectPath` argument against, since the .NET CLI resolves relative paths against the process's working directory identically to `Path.GetFullPath`) equals the Agent's resolved workspace path — proving both consume the same physical directory, without rerunning the full Phase 18.14 security/path matrix (per spec §15, one targeted assertion).

## 11. Tests

- **`ProjectWorkspaceProvisionerTests.cs`** (new, 11 tests): missing-directory creation, idempotent repeat provisioning, already-existing directory accepted, existing file rejected without throwing, invalid/whitespace/null RootPath rejected, `AllowedRoot` honored (inside succeeds; sibling/parent/`../` traversal outside all rejected, no directory created), and a provisioner/resolver agreement assertion (spec §10).
- **`ProjectServiceTests.cs`** (extended, +2 tests): `CreateAsync` results in the workspace directory existing on disk; a failing `IProjectWorkspaceProvisioner` test double causes `CreateAsync` to throw `IOException` with no Project row persisted (verified via `GetBySlugAsync` returning `null`).
- **`WorkItemExecutionIntegrationTests.cs`** (extended, +1 integration test + a `CreateOrchestrator` optional-`AgentWorkspaceOptions` parameter): real `ProjectService.CreateAsync` → real provisioned workspace → WorkItem dispatch → `AgentOrchestrator` resolves and executes successfully, using a `FakeAgentRuntime` (no real Hermes required for this deterministic test), plus the verification-compatibility assertion from §10.

## 12. Real-Hermes Result

Not executed. No Docker/real-Hermes environment was invoked for this phase; per spec §16, no result is fabricated. The existing `RealHermesE2ETests.cs`/`RealHermesApiE2ETests.cs` files (already gated behind their own environment checks, unmodified by this phase) will now benefit from real provisioning automatically the next time they run in an environment with Docker available, since they construct `Project`s through `ProjectService.CreateAsync` without a custom provisioner (picking up the new real default).

## 13. Migration Status

Zero migration, as required. `dotnet ef migrations has-pending-model-changes --startup-project ../AI.Factory.Api --project .` (run from `apps/src/AI.Factory.Infrastructure`) reported: `No changes have been made to the model since the last migration.` No new Project field was added — `RootPath` already existed and remains the sole source of truth.

## 14. Remaining Execution Gaps

1. **`AllowedRoot` + default relative `RootPath` interaction** (§5 above) — configuring `AllowedRoot` requires the process working directory to resolve inside it, or every new Project's default `RootPath` will fail provisioning. Pre-existing Phase 18.14 resolution behavior, first exercised by this phase, left unchanged per the approved design (extract verbatim, do not alter behavior).
2. **`GenerateSlug` path-traversal risk** (explicitly out of scope, not fixed): `ProjectService.GenerateSlug` only lowercases and replaces spaces with dashes — a crafted project `name` (e.g. containing `../` or path separators) could theoretically produce a slug that, combined with `RootPath = $"projects/{slug}"`, resolves outside the intended `projects/` tree. `WorkspacePathGuard`/`AllowedRoot` (when configured) would still catch and reject such a case at provisioning time — so this is defense-in-depth already covering the risk when `AllowedRoot` is set — but with `AllowedRoot` unset (the default), nothing currently rejects a traversal-shaped slug. Left untouched per explicit instruction; a real remaining risk to address in a future phase (e.g., sanitizing `GenerateSlug`'s output, or making `AllowedRoot` effectively mandatory in production).
3. Repository/source checkout remains unimplemented, as intended (§8) — a freshly provisioned workspace is empty; a coding agent's very first action against a new Project must currently be creating files from nothing, not modifying an existing checkout.

## 15. Validation

```text
dotnet build apps/AI.Factory.slnx -c Debug     -> Build succeeded. 0 Warning(s), 0 Error(s).
dotnet build apps/AI.Factory.slnx -c Release   -> Build succeeded. 0 Warning(s), 0 Error(s).
dotnet test  apps/AI.Factory.slnx -c Debug --no-build
    AI.Factory.Worker.Tests.dll      : 84 passed, 0 failed, 0 skipped
    AI.Factory.Application.Tests.dll : 742 passed, 0 failed, 0 skipped
    AI.Factory.Api.Tests.dll         : 293 passed, 0 failed, 0 skipped
    Total: 1119 passed, 0 failed, 0 skipped

dotnet ef migrations has-pending-model-changes --startup-project ../AI.Factory.Api --project .
    -> "No changes have been made to the model since the last migration."

grep -rn "Directory\.CreateDirectory|RootPath =" apps/src
    -> exactly one RootPath-assignment site (ProjectService.cs) and one
       Project-workspace Directory.CreateDirectory site (ProjectWorkspaceProvisioner.cs);
       all other hits are unrelated (backup dir, worker health dir, Planner-only
       Hermes scratch fallback). No second Project-workspace-creation path exists.
```

## 16. Files Changed

- `apps/src/AI.Factory.Application/Abstractions/IProjectWorkspaceProvisioner.cs` (new)
- `apps/src/AI.Factory.Application/Orchestration/ProjectWorkspaceProvisioner.cs` (new)
- `apps/src/AI.Factory.Application/Orchestration/ProjectWorkspaceResolver.cs` (refactored: extracted `WorkspacePathResolution.ResolveBoundedPath`, behavior unchanged)
- `apps/src/AI.Factory.Application/Projects/ProjectService.cs` (constructor + `CreateAsync` provisioning integration, `EnsureWorkspaceAsync` added)
- `apps/src/AI.Factory.Api/Program.cs` (DI registration for `IProjectWorkspaceProvisioner`)
- `apps/tests/AI.Factory.Application.Tests/ProjectWorkspaceProvisionerTests.cs` (new)
- `apps/tests/AI.Factory.Application.Tests/ProjectServiceTests.cs` (extended)
- `apps/tests/AI.Factory.Application.Tests/WorkItemExecutionIntegrationTests.cs` (extended)

`AI.Factory.Worker/Program.cs` was deliberately **not** modified — it registers `IProjectRepository`/`IWorkspaceResolver` but never `ProjectService` itself (verified by grep); there is nothing in the Worker process that constructs a `ProjectService`, so no provisioner registration is needed there.

---

Project Workspace Provisioning: VERIFIED
Final Decision: GO
