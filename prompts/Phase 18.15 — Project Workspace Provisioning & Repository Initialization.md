# Phase 18.15 — Project Workspace Provisioning & Repository Initialization

## Phase Objective

Close the concrete execution gap identified in Phase 18.14:

`Project.RootPath` is currently assigned by `ProjectService.CreateAsync` but the corresponding directory is not created on disk.

As a result, a real Project created through the API can reach WorkItem execution only to fail deterministically at workspace resolution because the authoritative workspace does not exist.

Phase 18.14 intentionally did not solve provisioning. This phase must now make a newly created Project's workspace actually usable by the unified AgentRun execution pipeline.

Target:

```text
Create Project
    ->
Provision Project.RootPath
    ->
workspace exists
    ->
WorkItem dispatch
    ->
AgentRun
    ->
Hermes
    ->
agent modifies workspace
    ->
VerificationGate builds/tests same workspace
```

This phase is about **workspace provisioning**, not full source-control/repository integration.

---

## 1. Repository Inspection

Before modifying code, inspect:

- `AGENTS.md`
- Phase 18.10–18.14 reports
- `ProjectService.CreateAsync`
- `Project`
- `Project.RootPath`
- `ProjectRepository`
- `IWorkspaceResolver`
- `ProjectWorkspaceResolver`
- `AgentWorkspaceOptions`
- `WorkspacePathGuard`
- `TaskDispatchScheduler`
- `AgentOrchestrator`
- `HermesAgentRuntime`
- `HermesAgentExecutor`
- `VerificationGate`
- `DotNetBuildExecutor`
- `DotNetTestExecutor`
- artifact infrastructure
- Docker configuration
- application startup/configuration
- existing filesystem abstractions
- tests covering Project creation and Agent execution

Search the repository for:

```text
RootPath =
Directory.CreateDirectory
DirectoryInfo
FileSystem
Workspace
ProjectService.CreateAsync
projects/
```

Determine whether an existing filesystem abstraction should be reused.

---

## 2. Provisioning Responsibility

Introduce a small Factory-owned provisioning abstraction only if an equivalent does not already exist.

Preferred conceptual contract:

```csharp
IProjectWorkspaceProvisioner
```

with responsibility limited to:

- creating the Project workspace directory;
- validating the target path;
- ensuring it is available for subsequent execution;
- returning deterministic success/failure.

Do not make this component responsible for:

- Git;
- cloning repositories;
- building code;
- running tests;
- Agent execution;
- Project lifecycle transitions.

Its single responsibility is workspace provisioning.

---

## 3. Project Creation Flow

The preferred creation flow is:

```text
ProjectService.CreateAsync
        |
        v
generate RootPath
        |
        v
validate / provision workspace
        |
        v
persist Project
```

Determine whether provisioning should happen:

### A. Before persistence

or:

### B. After persistence with compensating failure handling.

Choose based on existing transaction and filesystem conventions.

Do not pretend filesystem operations participate in the database transaction.

The final implementation must avoid a Project that is reported as successfully created when its required workspace could not be provisioned.

---

## 4. Path Safety

Reuse the Phase 18.14 workspace security model.

The provisioner must:

- resolve the path deterministically;
- use `WorkspacePathGuard` where appropriate;
- honor `AgentWorkspaceOptions.AllowedRoot` when configured;
- reject paths outside the configured boundary;
- never accept a workspace path directly from API input.

The Project's `RootPath` remains Factory-owned.

Do not add a second security/path-validation algorithm.

---

## 5. Directory Creation

At minimum, a successfully created Project must have:

```text
Project.RootPath
```

as an existing directory.

Prefer idempotent directory creation:

```text
directory absent -> create
directory already exists -> success
```

Do not treat an existing empty directory as an error.

However, detect and reject the case where the target path exists as a file rather than a directory.

Failure must be deterministic and observable.

---

## 6. Project Creation Failure Semantics

Define exact behavior for:

- invalid RootPath;
- AllowedRoot violation;
- permission denied;
- path exists as a file;
- parent path unavailable;
- directory creation failure.

Preferred behavior:

```text
workspace provisioning fails
    ->
Project creation fails
    ->
no misleading successful API response
```

Avoid leaving partially initialized Project state in the database where practical.

If database/file-system atomicity cannot be guaranteed, document the exact compensation/cleanup behavior.

Do not silently continue with a Project whose workspace is unusable.

---

## 7. Existing Project Compatibility

Do not automatically attempt to provision every historical Project during application startup.

For existing Projects whose `RootPath` does not exist:

- preserve the current deterministic failure behavior during Agent execution;
- optionally provide a reusable provisioning service that can later repair such Projects;
- do not introduce a hidden startup migration or mass filesystem mutation.

This phase should not unexpectedly modify existing production data.

---

## 8. Workspace Layout

Determine whether the current system needs only:

```text
<Project.RootPath>/
```

or a minimal known structure.

Do not invent unnecessary directories.

If the new Agent pipeline can operate against an empty directory, keep the initial provisioned workspace empty.

Do not create fake source files, `.git`, build artifacts, or placeholder projects unless an existing contract requires them.

---

## 9. Repository Initialization

Do NOT implement a complete Git provider or repository checkout system in this phase.

First determine whether the current Project model already contains enough information to initialize source content.

If there is already a repository source abstraction, reuse it.

Otherwise classify repository initialization as:

```text
Future Phase
```

and explicitly document:

> Workspace provisioning creates a valid empty Project workspace; source checkout/clone is a separate capability.

Do not fake repository content merely to make tests pass.

---

## 10. Interaction with Agent Execution

After provisioning, a real WorkItem-backed AgentRun must no longer fail solely because:

```text
Project.RootPath does not exist
```

The expected flow becomes:

```text
Project created
    ->
RootPath directory exists
    ->
AgentOrchestrator.ResolveWorkspace succeeds
```

The existing Phase 18.14 workspace resolver remains authoritative.

Do not duplicate workspace resolution logic inside the provisioner.

Prefer:

```text
Provisioner creates
Resolver validates/reads
```

rather than two components making independent decisions about path validity.

---

## 11. Interaction with Verification

Verification must continue to use:

```text
Project.RootPath
```

without a second project path.

The same directory must be used by:

```text
Agent
VerificationGate
BuildExecutor
TestExecutor
```

where applicable.

Do not change build/test command policy.

Do not introduce a separate build workspace.

---

## 12. Manual OrchestrationRun

The compatibility API must benefit automatically from the same provisioned Project workspace.

No special provisioning path may exist for:

```text
/api/orchestration-runs
```

Manual execution must continue to use:

```text
Project.RootPath
```

through the same Phase 18.14 workspace resolver.

---

## 13. Concurrency

Project creation may race with another request or process attempting to provision the same path.

Directory creation must be safe under the expected deployment model.

Do not introduce application-wide locks.

Do not introduce static ownership maps.

Use normal filesystem semantics and idempotent directory creation where possible.

---

## 14. Recovery / Restart

Workspace provisioning itself should not depend on process-local memory.

After restart:

```text
Project.RootPath
    ->
Directory.Exists
```

must remain the authoritative reality.

If a Project workspace is externally deleted after creation, Agent execution should still fail deterministically through the existing Phase 18.14 resolver.

Do not automatically recreate deleted workspaces during every AgentRun unless there is an explicit product reason to do so.

Provisioning should be a creation concern, not a hidden execution side effect.

---

## 15. Tests

Add focused tests.

### Provisioner tests

Verify:

- creates missing directory;
- repeated provisioning succeeds idempotently;
- existing directory is accepted;
- existing file at target path is rejected;
- invalid path is rejected;
- configured AllowedRoot is honored;
- sibling/parent/outside paths are rejected;
- provisioning failure is deterministic.

### ProjectService tests

Verify:

```text
Create Project
    ->
workspace exists on disk
```

Verify failure behavior when provisioning fails.

### Integration test

Create a real temporary Project through the actual ProjectService, then:

```text
Project.RootPath exists
    ->
create WorkItem
    ->
dispatch WorkItem
    ->
AgentOrchestrator resolves workspace
```

Use a fake runtime; do not require real Hermes for the deterministic integration test.

### Verification compatibility

Prove that VerificationGate receives the same Project.RootPath.

Do not rerun the full Phase 18.14 workspace/security matrix unless a modified behavior requires regression coverage.

---

## 16. Docker / Real Hermes Test

Do not build new Docker infrastructure solely for this phase.

However, if the existing real-Hermes environment is available, add or run a focused test that proves the entire practical chain:

```text
Project created
    ->
workspace provisioned
    ->
WorkItem dispatched
    ->
real Hermes container mounts Project.RootPath
    ->
agent observes Project workspace
    ->
agent modifies a controlled test file
    ->
VerificationGate sees the modification
```

A marker-file or similarly deterministic test is preferred.

Do not allow a real LLM to perform arbitrary destructive operations.

Never fabricate the result if Docker/provider access is unavailable.

---

## 17. Existing Project Repair / Backfill

Do not automatically backfill all historical Projects.

However, if a minimal, reusable operation such as:

```text
EnsureWorkspaceAsync(projectId)
```

fits naturally into the existing service architecture, it may be added for future operational use.

Do not create a public endpoint solely for this phase unless the repository already has an established pattern for administrative workspace provisioning.

---

## 18. Migration Policy

Prefer:

```text
zero migration
```

No new Project field is required.

`Project.RootPath` already exists and remains the source of truth.

---

## 19. No Scope Creep

Do not:

- redesign Project lifecycle;
- redesign AgentRun;
- redesign TaskDispatchScheduler;
- redesign VerificationGate;
- implement true cancellation/preemption;
- reintroduce `AutonomousCodingOrchestrator`;
- implement full Git integration;
- implement remote repository providers;
- implement project templates;
- create a new artifact storage system;
- redesign Docker execution.

This phase is specifically:

```text
Project workspace provisioning
+
making the existing execution pipeline practically usable
```

---

## 20. Validation

Run focused tests first.

Then:

```bash
dotnet build AI.Factory.slnx -c Debug
dotnet build AI.Factory.slnx -c Release
dotnet test AI.Factory.slnx -c Debug --no-build
```

Run:

```bash
dotnet ef migrations has-pending-model-changes \
  --startup-project ../AI.Factory.Api \
  --project .
```

Also verify the repository contains no alternative Project workspace creation path that can produce a different directory from `Project.RootPath`.

---

## 21. Failure Handling / NO-GO

Return **NO-GO** if:

- a successfully created Project can still have a missing required workspace;
- Project creation reports success despite provisioning failure;
- workspace creation bypasses `WorkspacePathGuard` when an AllowedRoot is configured;
- API input can influence the provisioned filesystem path;
- Agent execution uses a different directory than the provisioned Project.RootPath;
- Verification uses a different directory from the Agent workspace;
- provisioning silently creates arbitrary fallback directories;
- a second workspace authority is introduced;
- repository checkout is faked or silently assumed.

---

## 22. Final Report

Create:

```text
.ai/context/phase-18.15-project-workspace-provisioning.md
```

Document:

- current provisioning gap;
- filesystem dependency analysis;
- provisioner abstraction;
- Project creation integration;
- path-security behavior;
- failure semantics;
- existing Project behavior;
- repository initialization decision;
- AgentRun integration;
- verification compatibility;
- tests;
- real-Hermes result if executed;
- migration status;
- remaining execution gaps.

---

## 23. Definition of Done

The minimum successful lifecycle is:

```text
POST /api/projects
        |
        v
Project persisted
        |
        v
Project.RootPath provisioned
        |
        v
Directory exists
        |
        v
WorkItem Ready
        |
        v
TaskDispatchScheduler
        |
        v
AgentRun
        |
        v
AgentOrchestrator
        |
        v
IWorkspaceResolver
        |
        v
Project.RootPath
        |
        v
Hermes
```

No Project is considered successfully created while its required workspace cannot be provisioned.

The workspace remains Factory-owned and deterministic.

Final decision must be exactly:

```text
Project Workspace Provisioning: VERIFIED
Final Decision: GO
```

or:

```text
Project Workspace Provisioning: NOT VERIFIED
Final Decision: NO-GO
```

Core principle:

> Project creation establishes the workspace.
> Workspace resolution validates the workspace.
> Agent execution uses that workspace.
> Verification tests that same workspace.
> Repository checkout is a separate concern and must not be faked into this phase.