# Phase 18.16 — Project Source Initialization & Repository Bootstrap

## 1. Repository / Source Architecture Findings

Before coding, confirmed by direct inspection (not assumption):

- `Project.GitRepository` / `Project.GitBranch` (`AI.Factory.Domain/Entities/Project.cs`) already existed since early migrations, **unused by any production code** — confirmed by grep. Reused as-is; no schema change.
- `CommandPolicyOptions.AllowedCommands` defaulted to `{ "docker", "dotnet" }` — `git` was not allowed. `SystemProcessRunner` already executes via `ProcessStartInfo.ArgumentList` (no shell interpreter) — confirmed the only `ProcessStartInfo` construction site in the whole solution.
- `ProjectTransitions.Created -> Analyzing` already existed and was unused — the natural "source decision made" edge, requiring zero change to `ProjectTransitions.cs`.
- `WorkItemService.CreateAsync` had **zero** gating on `Project.Status` — a WorkItem could be created against a freshly-`Created` Project with no source decision.
- `VerificationGate` called `_buildExecutor`/`_testExecutor` with the **raw, un-resolved** `project.RootPath`, while `AgentOrchestrator` (Phase 18.14) resolved the same `RootPath` through `IWorkspaceResolver` — two independently-CWD-dependent resolutions that only coincidentally agreed.
- `IWorkspaceResolver`/`ProjectWorkspaceResolver`/`WorkspacePathGuard`/`WorkspacePathResolution` (Phase 18.14/18.15, already uncommitted in the tree) were confirmed as the single shared path-resolution algorithm.
- `IProcessRunner` is declared in `AI.Factory.Infrastructure.Agents` (Infrastructure layer), consumed today only by `DotNetBuildExecutor`/`DotNetTestExecutor` (also Infrastructure), which implement Application-layer abstractions (`IBuildExecutor`/`ITestExecutor`). This confirmed the Clean Architecture pattern to follow: `IProjectSourceInitializer` declared in `Application.Abstractions`, implemented in `Infrastructure` (not Application) — `AI.Factory.Application.csproj` only references `AI.Factory.Domain`, confirming Application cannot reference Infrastructure directly.
- A **pre-existing, out-of-scope gap** was found during the required repo-wide search: `AI.Factory.Infrastructure/Context/LocalContextProvider.cs` independently calls `Path.GetFullPath(project.RootPath)` twice (lines ~123–128, ~165–170), bypassing `IWorkspaceResolver` entirely. This predates Phase 18.16, is not part of the Agent-execution/Verification chain this phase's Definition of Done governs, and touching it was out of the explicit scope given (no Planner/Context redesign authorized). Documented as a remaining gap below; a background-task suggestion was filed separately.

## 2. Selected Source Initialization Model

Exactly two source types, per the confirmed requirements — `ProjectSourceType.Empty | GitRepository`. No template source, no provider-specific classes (GitHub/GitLab/ADO), no private-repo credentials. `SourceSpecification(SourceType, Location?, Branch?)`.

## 3. Source Abstraction

- `AI.Factory.Application/Abstractions/IProjectSourceInitializer.cs` — `Task<SourceInitializationResult> InitializeAsync(Project, SourceSpecification, CancellationToken)`, plus `ProjectSourceType`, `SourceSpecification`, `SourceInitializationFailureKind`, `SourceInitializationResult`.
- `AI.Factory.Infrastructure/Source/ProjectSourceInitializer.cs` — the one implementation. Resolves the workspace via the SAME `IWorkspaceResolver` Agent/Verification use (never a second workspace authority), validates the Git spec, refuses a non-empty workspace, executes `git` only via `IProcessRunner`'s argument-list API, classifies every outcome into `SourceInitializationFailureKind` (`Validation` / `Conflict` / `Infrastructure`), and never mutates `Project` itself — persistence decisions stay in `ProjectService`.

**`SourceInitializationFailureKind` — the exact HTTP status mapping and why:**

| FailureKind | HTTP | Rationale |
|---|---|---|
| `Validation` | 400 (`ValidationException`) | Malformed/missing request input (missing `Location`, leading-dash argument-injection attempt) — a client input problem. |
| `Conflict` | 409 (`ConflictException`) | Non-empty target workspace, or a repeated/already-decided source call — a state-conflict, matching the existing `ConflictException` convention used by `ProjectService.ApproveAsync`/`TransitionStatusAsync`. |
| `Infrastructure` (default) | 503 (`IOException` → `IFailureClassifier` → `FailureCategory.Infrastructure`, transient) | git executable unavailable, clone timeout, or any non-zero git exit (nonexistent repository, nonexistent branch, network failure) — all folded into ONE bucket deliberately: git returns the same generic non-zero exit code for many distinct causes, and parsing stderr text to split "repository not found" from "branch not found" from "network failure" would be fragile and non-deterministic across git versions/locales. This mirrors the exact `IOException`/transient-`Infrastructure`/503 convention `ProjectService.CreateAsync` already uses for workspace-provisioning failures — no new mapping vocabulary invented. |

A resolved-workspace failure (should not normally happen post-provisioning) also falls into `Infrastructure`.

## 4. Project Creation / Lifecycle Integration

- `POST /api/projects` unchanged — still only provisions the workspace, `Status = Created`.
- New `POST /api/projects/{id}/source` → `ProjectService.InitializeSourceAsync`:
  - 404 if the Project doesn't exist.
  - 409 if `Status != Created` (covers BOTH "repeated call after success" and "the decision is final" with one rule).
  - Calls `IProjectSourceInitializer.InitializeAsync`; on failure, maps `FailureKind` to the exception per the table above and returns WITHOUT touching `Project` at all.
  - On success: `GitRepository`/`GitBranch` persisted only for `GitRepository` sourcetype; then `project.TransitionTo(ProjectStatus.Analyzing)` (the existing, previously-unused `Created -> Analyzing` edge) and `SaveChangesAsync`.
- `WorkItemService.CreateAsync` now rejects (`ConflictException`, 409) creating a WorkItem against a Project still at `Status == Created` — the concrete enforcement that a Project cannot enter the execution path before its source decision is made. `ProjectTransitions.cs` itself was **not modified**.
- `ProjectService` constructor gained one more **optional** trailing parameter, `IProjectSourceInitializer? sourceInitializer = null` — fully backward compatible with every existing 1–3-arg call site. Unlike the workspace provisioner, no in-Application-layer default instance is synthesized here (the real implementation needs `IProcessRunner`, an Infrastructure concern) — `InitializeSourceAsync` throws `InvalidOperationException` if called without one configured; production DI (`Api/Program.cs`) always supplies the real one.

## 5. Git Behavior

- `git` added to `CommandPolicyOptions.AllowedCommands` default (`docker, dotnet, git`) and to both `Api`/`Worker` `appsettings.json` `CommandPolicy.AllowedCommands` (config binding replaces the array, so both needed the explicit addition).
- Destination is always the Factory-resolved `Project.RootPath` — the request DTO carries no destination field at all.
- Arguments built as an explicit list: `["clone", ("--branch", branch, "--single-branch")?, "--", location, resolvedPath]` — `location`/`branch` are always positional arguments through `IProcessRunner`'s argument-LIST API (`ProcessStartInfo.ArgumentList`, `UseShellExecute = false`) — never a shell string.
- **Argument-injection hardening (not explicitly spelled out by the user, added because it's a real, well-known Git vulnerability class):** `Location`/`Branch` are rejected with a `Validation` failure if either starts with `-`. Rationale: even through an argument LIST (no shell involved), a value like `--upload-pack=/some/command` passed as a positional argument can still be parsed by **git's own argv parser** as an option flag, not a value — this is a documented git CVE class (argument/option injection), independent of shell-escaping concerns. The `--` separator before the positional arguments is added as a second, defense-in-depth layer.
- Non-empty-workspace policy: `Directory.EnumerateFileSystemEntries(resolvedPath).Any()` before ever invoking git — returns `Conflict` if true, never overwrites, never deletes pre-existing files.
- Partially-initialized-checkout cleanup (spec §11): on a failed/timed-out clone, `CleanupPartialClone` removes only entries found in the workspace **at that point** — safe specifically because this method is only reached after the same call already confirmed the workspace was completely empty immediately before invoking git, so anything present afterward was created by *this* failed attempt, never pre-existing content. Implemented with a small bounded retry (max 3 attempts, short backoff) to tolerate a transient Windows file-handle-release race right after the git process exits; best-effort only — a cleanup failure is logged but never masks the original clone-failure result.

## 6. Slug / Path Hardening

`ProjectService.GenerateSlug` rewritten: every character outside `[a-z0-9]` (case-folded first) collapses to a single `-`; repeated separators collapse further; leading/trailing separators trimmed. This structurally destroys `/`, `\`, `..`, `:`, spaces, punctuation, and all non-ASCII/Unicode input by construction — they can only ever become the `-` separator, never survive into the slug. A name that normalizes to nothing falls back to a deterministic, non-empty `"project-{first-12-hex-of-SHA256(originalName)}"` — same input always regenerates the same fallback slug (verified by a dedicated determinism test), and the fallback never itself contains a separator/traversal character. `Project.RootPath = "projects/{slug}"` is therefore always a safe, single-segment child path.

A known, accepted side effect: a stricter charset means previously-distinct names can now collide (e.g. `"My Project!"` and `"My Project?"` both → `my-project`) — the *existing* slug-uniqueness check (`GetBySlugAsync` → `ConflictException`) already handles this correctly, exactly as it would for two identically-named Projects; a dedicated test proves this.

## 7. `AllowedRoot` / `ProjectsBaseDirectory` Behavior

- `AgentWorkspaceOptions.ProjectsBaseDirectory` (new, optional `string?`) added. Resolution order inside the single shared `WorkspacePathResolution.ResolveBoundedPath` helper: RootPath → (if `ProjectsBaseDirectory` configured AND RootPath is relative) resolve against a once-per-call `Path.GetFullPath(ProjectsBaseDirectory)` → `Path.GetFullPath` → optional `AllowedRoot` boundary via the existing `WorkspacePathGuard` (unchanged) → existence check (resolver only).
- **Unset (default) reproduces the EXACT prior CWD-relative behavior** — verified by a dedicated regression test (`Resolve_ProjectsBaseDirectoryUnset_ReproducesPriorCwdRelativeBehavior`) in addition to every pre-existing `WorkspaceResolverTests`/`ProjectWorkspaceProvisionerTests` case continuing to pass unmodified.
- An already-absolute `RootPath` is completely unaffected by `ProjectsBaseDirectory` either way.
- `AgentWorkspaceOptions.Validate()` added, following the exact convention already used by `OrchestrationOptions`/`ExecutionLimitsOptions`/`ShutdownOptions` (instantiate → bind → `.Validate()` → register); wired into both `Api/Program.cs` and `Worker/Program.cs` right after binding. A malformed configured `ProjectsBaseDirectory` (one `Path.GetFullPath` rejects) throws `InvalidOperationException` at startup — never silently reinterpreted.
- Cross-component proof: `Provision_ProjectsBaseDirectoryConfigured_ProvisionerAndResolverAgree` (Provisioner ↔ Resolver) and the end-to-end `ProjectSourceIntegrationTests` test (Provisioner ↔ Initializer ↔ Resolver ↔ `AgentOrchestrator` ↔ the same resolver `VerificationGate` would use) all resolve to the identical absolute path with a configured base directory.
- Changing the process CWD after configuring a base does not change resolution (`Resolve_ProjectsBaseDirectoryConfigured_ChangingProcessCwd_DoesNotChangeResolution`).
- A configured base outside a configured `AllowedRoot`, and a traversal-shaped RootPath under a configured base, both fail deterministically via the unchanged `WorkspacePathGuard` — no second traversal/symlink implementation was introduced.

## 8. VerificationGate — the Central Invariant

`VerificationGate` now takes `IWorkspaceResolver` as a constructor dependency and resolves the workspace ONCE per `VerifyAsync`/`VerifyProjectAsync` call, using the resolved absolute path for both `BuildRequest`/`TestRequest` instead of raw `project.RootPath`. A resolution failure returns `VerificationStatus.InfrastructureError` immediately — **no silent CWD fallback**. This makes "Agent workspace == Verification workspace" true by shared resolution, not coincidence.

One real behavioral consequence surfaced and was deliberately accepted: `VerificationGateRealBuildIntegrationTests`'s "missing project path" case previously reached `dotnet build` and failed there with a nonzero exit (`VerificationStatus.Failed`); it now fails at the shared resolution step instead (`VerificationStatus.InfrastructureError`, no process ever launched) — renamed and re-asserted accordingly. The same file's "real build" test was changed to point `Project.RootPath` at the Domain project's **directory** rather than its `.csproj` file, because `IWorkspaceResolver` requires an existing directory (the same rule Agent workspace resolution already enforced) — `dotnet build`/`dotnet test` both accept a directory and locate the single project file inside it, so this is a test-fixture correction, not a behavior change.

## 9. Workspace Safety / Existing-Directory Policy

Unchanged single authority: `WorkspacePathGuard.EnsureWithinRoot` (traversal/symlink-resolving) is the only boundary check anywhere in the solution; `ProjectSourceInitializer` never introduces a second one. Existing-directory policy: empty → initialize; non-empty → deterministic `Conflict` refusal, verified with a real pre-existing user file surviving a rejected clone attempt.

## 10. Failure Semantics

All named scenarios in spec §11 map to a `SourceInitializationResult`/HTTP outcome and are covered by tests: invalid source (`Validation`/400), git unavailable (`ProcessLaunchException` → `Infrastructure`/503), branch/repository missing (`Infrastructure`/503), destination conflict (`Conflict`/409), partially-initialized checkout (best-effort cleanup, §5 above), repeated initialization (`Conflict`/409 both at the `ProjectService.Status != Created` gate and — belt-and-suspenders — at the initializer's own non-empty-workspace check). `GitRepository`/`GitBranch` are written to `Project` ONLY after `SourceInitializationResult.Success == true`; a failed call transitions nothing and persists nothing, proven directly by an API test that fails an initialization attempt, then confirms the Project is still `Created` and can still be successfully retried.

## 11. Security / Credential Handling

No credential fields added anywhere; public-repository Git only. Private-repository authentication is explicitly a documented future capability (this report), not implemented. `git` is added to the process allow-list but the deny-list (`rm, del, curl, wget, powershell, ...`) is untouched. No new shell/process execution abstraction was created — `IProcessRunner`/`SystemProcessRunner` remain the sole path, confirmed by a repo-wide `ProcessStartInfo` search returning exactly one construction site.

## 12. Tests

- **Slug/path safety** (`ProjectServiceTests.cs`): normal names, spaces, punctuation, Unicode (Latin + CJK), `../`, `..\`, multiple separators, drive-letter-like (`C:\`), absolute-path-like (`/...`), empty-after-normalization fallback + determinism, and the accepted post-hardening slug-collision-throws-Conflict case.
- **`AgentWorkspaceOptions`** (`AgentWorkspaceOptionsTests.cs`): unset/valid/malformed `ProjectsBaseDirectory` validation.
- **`ProjectWorkspaceResolver`** (`WorkspaceResolverTests.cs`, extended): unset-base regression, relative-under-base, absolute-unaffected, CWD-change-independence, base+`AllowedRoot` together, base-outside-`AllowedRoot`, traversal-under-base rejection.
- **`ProjectWorkspaceProvisioner`** (`ProjectWorkspaceProvisionerTests.cs`, extended): Provisioner/Resolver agreement with a configured base directory.
- **`VerificationGate`** (`VerificationGateTests.cs`, extended): resolved-path-used-not-raw-RootPath (recording fakes), resolution-failure → `InfrastructureError` for both `VerifyAsync`/`VerifyProjectAsync`; (`VerificationGateRealBuildIntegrationTests.cs`, adjusted): real build/test against the resolver-required directory shape, missing-path now asserts `InfrastructureError`.
- **`ProjectSourceInitializer`** (`ProjectSourceInitializerTests.cs`, new, 11 tests): a real local bare-Git-repo fixture (seeded via real `git` commands — `git --version` confirmed available before relying on it) with two branches/commits; default-branch clone, explicit-branch checkout (verified via `git rev-parse --abbrev-ref HEAD`), Empty success, missing location, leading-dash location/branch injection attempts, non-empty-workspace conflict (pre-existing file survives), repeated initialization, nonexistent repository, nonexistent branch, workspace-resolution failure.
- **`ProjectService.InitializeSourceAsync`** — covered end-to-end via the API tests below plus the integration test.
- **API** (`ProjectSourceApiTests.cs`, new, 9 tests): successful Git init (real local repo), successful Empty init, invalid source type (400), missing Git location (400), nonexistent repository (503), invalid branch (503), unknown Project (404), repeated initialization (409), and the explicit "failed attempt never falsely marks Project initialized, and remains retryable" test.
- **WorkItem gating**: `WorkItemService.CreateAsync` rejection on `Status == Created` is exercised by every one of the ~13 pre-existing test files whose Project-seeding helpers were updated (see §14) plus is implicit in every `ProjectSourceApiTests`/`ProjectsAndTasksApiTests` flow that now must initialize source before creating a task.
- **Integration** (`ProjectSourceIntegrationTests.cs`, new): the full chain — `ProjectService.CreateAsync` (real provisioner) → `InitializeSourceAsync` (real Git clone into the provisioned `RootPath`) → real cloned file present → `WorkItemService.CreateAsync`/dispatch via `TaskDispatchScheduler` → `AgentOrchestrator` (fake Hermes runtime, no real Hermes) resolves the **identical** absolute path the Provisioner used → a fresh `ProjectWorkspaceResolver.Resolve` call (what `VerificationGate` itself now uses) agrees on that same path and still sees the real cloned file. Run with a configured `ProjectsBaseDirectory`, exercising the Phase 18.16 path-hardening dimension end-to-end in the same test.

## 13. Local Repository Integration Result

`git version 2.55.0.windows.5` confirmed present before relying on it. All local-fixture-based tests (`ProjectSourceInitializerTests`, `ProjectSourceApiTests`, `ProjectSourceIntegrationTests`) ran against real `git` commands and passed. No GitHub/internet dependency anywhere in the automated suite.

One real Windows-specific issue was found and fixed during this work: `Directory.Delete(recursive: true)` on a directory containing git's own object-store files throws `UnauthorizedAccessException` (git marks blobs read-only on Windows) — both test cleanup helpers and the production `ClearReadOnlyRecursive`/`CleanupPartialClone` logic clear the read-only attribute first. A second, purely cosmetic Windows/git timing quirk was also found: git's own internal cleanup of a failed clone's temporary lock file can leave a transient NTFS "delete-pending" directory-entry artifact visible for a moment after the git process exits; the relevant test asserts the absence of real repository content (`.git/HEAD`, `.git/objects`) rather than byte-for-byte directory emptiness, since chasing that OS-level artifact further would be over-engineering a courtesy cleanup step the spec itself only requires on a best-effort basis ("where cleanup is safe and deterministic").

## 14. Regression Fixes Required by the New `WorkItemService` Gate

Gating `WorkItemService.CreateAsync` on `Project.Status != Created` broke every existing test/fixture that created a WorkItem immediately after `ProjectService.CreateAsync` without an intervening status transition. Fixed by advancing each affected Project straight to `Analyzing` in its seed helper (mirroring the existing `SeedProjectAtStatusAsync` pattern already used elsewhere), or — for the shared HTTP-level `ProjectsAndTasksApiTests.CreateProjectAsync` helper — auto-initializing an `Empty` source by default (with an explicit `initializeSource: false` opt-out for the three status-transition tests that specifically need to observe the raw `Created` state). Files touched: `WorkItemServiceTests.cs`, `TaskDependencyServiceTests.cs`, `TaskReadinessServiceTests.cs`, `AgentRunHeartbeatRecoveryTests.cs`, `AgentRunProjectTaskLinkageTests.cs`, `AgentRunWorkItemSyncTests.cs`, `TaskDispatchSchedulerTests.cs`, `RepairDispatchTests.cs`, `RepairVerificationLoopTests.cs`, `WorkItemExecutionIntegrationTests.cs`, `RealHermesE2ETests.cs` (Worker.Tests, gated behind its existing `Enabled` flag), `ProjectsAndTasksApiTests.cs`. No test assertion's actual *intent* changed — every fix was purely "make the Project execution-ready before exercising WorkItem behavior," which is exactly what the new gate now requires of real callers too.

## 15. Schema / Migration Result

`dotnet ef migrations has-pending-model-changes --startup-project ../AI.Factory.Api --project .` → **"No changes have been made to the model since the last migration."** Zero migration, exactly as required — `GitRepository`/`GitBranch` were already-existing columns, and `ProjectsBaseDirectory` is configuration-only.

## 16. Validation — Exact Results

```
dotnet build apps/AI.Factory.slnx -c Debug    → Build succeeded. 0 Warning(s), 0 Error(s).
dotnet build apps/AI.Factory.slnx -c Release  → Build succeeded. 0 Warning(s), 0 Error(s).
dotnet test  apps/AI.Factory.slnx -c Debug --no-build
    AI.Factory.Worker.Tests:       84 passed, 0 failed,  84 total
    AI.Factory.Api.Tests:         302 passed, 0 failed, 302 total
    AI.Factory.Application.Tests: 787 passed, 0 failed, 787 total
    TOTAL: 1173 passed, 0 failed, 1173 total
dotnet ef migrations has-pending-model-changes --startup-project ../AI.Factory.Api --project .
    → No changes have been made to the model since the last migration.
```

One pre-existing test (`ProjectApprovalApiTests.Approve_ConcurrentRequests_BothSucceed_ProjectIsCompleted`, unrelated to this phase — a SQLite-concurrency-sensitive test predating Phase 18.16) failed once under full-suite parallel load with a transient 503, then passed both in isolation and on a full-suite rerun — a known flake under load, not a regression introduced by this phase (confirmed: nothing in this phase touches `ApproveAsync`, its concurrency handling, or its SQLite access pattern).

Repository-wide searches (spec §24):
- `RootPath =` → exactly the Project-entity assignment in `ProjectService.CreateAsync` (plus an unrelated EF migration column definition).
- `ProjectsBaseDirectory` → confined to `AgentWorkspaceOptions` and `ProjectWorkspaceResolver`.
- `.RootPath\b` → every genuine resolution site is `ProjectWorkspaceResolver`/`WorkspacePathResolution` (the one shared algorithm) plus the pre-existing, out-of-scope `LocalContextProvider` gap noted in §1/§17.
- `ProcessStartInfo` → exactly one construction site (`SystemProcessRunner`) in the entire solution.
- `IProjectSourceInitializer` → one interface, one implementation, one DI registration (`Api/Program.cs`; `Worker/Program.cs` does not construct `ProjectService` at all, confirmed unchanged from the Phase 18.15 finding).

## 17. Remaining Gaps

- `AI.Factory.Infrastructure/Context/LocalContextProvider.cs` independently resolves `project.RootPath` via raw `Path.GetFullPath` + `Directory.Exists`, bypassing `IWorkspaceResolver` — pre-existing (not introduced by this phase), out of this phase's explicit scope (no Planner/Context redesign authorized), but worth a dedicated follow-up so exactly one workspace-resolution algorithm governs the whole codebase, not three independent ones colliding only by construction-time coincidence. Flagged separately as a background-task suggestion.
- Private-repository Git authentication remains explicitly unimplemented (documented future capability per spec §17) — no credential storage, no SSH key management exists anywhere in `Project` or `SourceSpecification`.
- Real-Hermes E2E execution against a Git-initialized (non-empty) `RootPath` was not run in this phase (no Docker/Hermes provider access available in this environment) — the system is left in the state required for the next phase to do so (`RealHermesE2ETests.cs` already updated to remain compatible with the new gate, behind its existing `Enabled` flag).

## 18. Final Decision

- Project source can be written outside `Project.RootPath`? No — the initializer only ever resolves through `IWorkspaceResolver` and never accepts a destination from the request.
- Project name can still generate an escaping filesystem path? No — `GenerateSlug` structurally allows only `[a-z0-9-]`, proven by a dedicated test covering every named traversal/separator/drive-letter/absolute-path example in the spec.
- Configured `AllowedRoot` bypassable through relative RootPath resolution? No — `ProjectsBaseDirectory` resolution happens strictly before the unchanged `AllowedRoot`/`WorkspacePathGuard` check, in the one shared helper.
- Repository initialization can overwrite an existing non-empty workspace without explicit policy? No — deterministic `Conflict` refusal, proven with a surviving pre-existing file.
- Failed initialization can leave the Project falsely executable? No — `GitRepository`/`GitBranch`/status transition are written only after `Success == true`; proven by a dedicated API test.
- Git execution uses unrestricted arbitrary shell input? No — `IProcessRunner`'s argument-list API only, plus argument-injection (leading-dash) hardening and a `--` separator.
- Source initialization introduces a second workspace authority? No — `IWorkspaceResolver` is the only path-resolution algorithm the initializer, the Agent, and Verification all now share (the LocalContextProvider gap noted in §17 predates this phase and is not part of the Agent/Verification chain this phase's Definition of Done governs).
- Agent and Verification use different source trees? No — `VerificationGate` now resolves through the same `IWorkspaceResolver`, proven identical to the Provisioner's/Orchestrator's resolution in the end-to-end integration test.
- Implementation recreates legacy copy-in/copy-out execution? No — git clones directly into the Factory-owned `RootPath`; no scratch directory, no copy step anywhere.
- Private credentials persisted insecurely? No — none are accepted, stored, or logged anywhere.

```
Project Source Initialization: VERIFIED
Final Decision: GO
```
