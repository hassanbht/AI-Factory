# Phase 18.16 — Project Source Initialization & Repository Bootstrap

## Phase Objective

Make a newly created Project contain real source code that the unified AgentRun pipeline can inspect and modify.

Phase 18.15 established a real, Factory-owned Project workspace:

```text
POST /api/projects
    -> Project.RootPath
    -> workspace directory exists
```

The remaining practical gap is:

```text
Project workspace
    -> EMPTY
```

A real coding-agent workflow requires:

```text
Project
    ->
Workspace
    ->
Source Code
    ->
WorkItem
    ->
AgentRun
    ->
Hermes
    ->
modify source
    ->
Verification
```

This phase must introduce the smallest clean mechanism for initializing Project source code without recreating the deleted legacy execution pipeline.

---

## 1. Repository Inspection

Before coding, inspect:

- `AGENTS.md`
- Phase 18.12–18.15 reports
- `Project`
- `ProjectService`
- `Project.RootPath`
- `ProjectWorkspaceProvisioner`
- `IWorkspaceResolver`
- `WorkspacePathGuard`
- `AgentWorkspaceOptions`
- all existing Git-related code
- Git command/process abstractions
- repository/provider abstractions
- project creation API/DTOs
- Artifact infrastructure
- `IProcessRunner`
- `CommandPolicyOptions`
- Docker/process security configuration
- existing tests

Search globally for:

```text
git
clone
checkout
repository
repositoryUrl
source
template
workspace
RootPath
GenerateSlug
```

Do not assume that no repository abstraction exists.

---

## 2. Define the Source Initialization Model

Determine what the current Project domain can support.

The source initialization mechanism should support at least one real deterministic source:

```text
Existing repository
```

or:

```text
Local/template source
```

Choose the smallest useful option based on existing repository capabilities.

Do NOT attempt to support every Git provider in this phase.

The phase should establish an abstraction that can later support:

```text
GitHub
GitLab
Azure DevOps
local Git
templates
```

without hard-coding all of them now.

---

## 3. Preferred Abstraction

If no equivalent exists, introduce a small application abstraction such as:

```csharp
IProjectSourceInitializer
```

Conceptually:

```csharp
Task<SourceInitializationResult> InitializeAsync(
    Project project,
    SourceSpecification source,
    CancellationToken cancellationToken = default);
```

Keep the responsibility limited to:

- obtaining the source;
- placing it into the already-provisioned `Project.RootPath`;
- validating successful initialization.

It must NOT own:

- Agent execution;
- WorkItem creation;
- build/test;
- verification;
- Project lifecycle transitions;
- scheduler behavior.

---

## 4. Source Specification

Do not expose arbitrary filesystem paths as the primary API contract.

Define a minimal source specification that can safely represent the supported source type.

For example, conceptually:

```text
SourceType
SourceLocation
Branch/Revision
```

Use the existing domain/API conventions if an equivalent exists.

Do not invent a large repository configuration hierarchy.

---

## 5. Project Creation Semantics

Determine whether source initialization belongs:

```text
A. during Project creation
```

or:

```text
B. immediately after Project creation through an explicit initialization operation
```

Prefer the design that avoids a Project being considered "ready for execution" before source initialization succeeds.

Do not pretend filesystem + Git/network operations are one database transaction.

If source initialization is separated from `CreateAsync`, make the state/progress explicit and deterministic.

---

## 6. Empty Project Support

Do not remove the ability to have an intentionally empty Project workspace.

If an empty Project is a legitimate scenario, preserve it explicitly.

The system should distinguish:

```text
Source not initialized yet
```

from:

```text
Intentionally empty project
```

using existing Project lifecycle/state semantics where possible.

Do not introduce ambiguous magic values.

---

## 7. Repository Checkout / Clone

If Git is the selected source mechanism:

- execute Git only through the repository's existing process abstraction;
- never execute an LLM-generated Git command;
- use `Project.RootPath` as the destination;
- prevent checkout outside the Factory-approved workspace;
- do not allow arbitrary destination paths from API input;
- support deterministic branch/revision selection where practical;
- fail clearly when checkout cannot be completed.

Do not create a new unrestricted shell execution abstraction.

---

## 8. Security Hardening Required Before Source Import

Phase 18.15 identified two path risks that become more important once source initialization is introduced.

### RootPath / AllowedRoot

Fix the interaction where:

```text
Project.RootPath = "projects/{slug}"
```

is resolved relative to the process working directory before `AllowedRoot`.

The final behavior must be deterministic.

A configured:

```text
AgentWorkspaceOptions.AllowedRoot
```

must correctly govern the actual Project workspace regardless of process current directory.

Do not require operators to accidentally depend on the application's current working directory for security.

### Slug traversal

Harden `GenerateSlug` so that Project names cannot produce filesystem traversal or separator characters.

Examples that must not escape the intended Project workspace include:

```text
../foo
..\foo
../../secret
C:\outside
/absolute/path
```

Prefer a deterministic slug normalization policy producing a safe filesystem segment.

Do not rely solely on `AllowedRoot` as the only defense.

The Project name is API-reachable even though `RootPath` itself is Factory-generated.

---

## 9. Workspace Boundary

Preserve the Phase 18.14/18.15 rule:

```text
Project.RootPath
    ->
normalize
    ->
optional AllowedRoot validation
    ->
workspace
```

The source initializer must operate only inside this resolved workspace.

Do not introduce a second path validator.

---

## 10. Existing Directory Policy

Before importing source, determine what happens when:

```text
Project.RootPath
```

already contains files.

Do not blindly overwrite or delete existing source.

Prefer one of:

```text
empty workspace -> initialize
non-empty workspace -> deterministic refusal
```

or an existing repository-aware policy already present in the codebase.

Do not silently destroy source files.

---

## 11. Source Initialization Failure

Define deterministic outcomes for:

- invalid source;
- repository unavailable;
- authentication failure;
- branch/revision missing;
- network failure;
- destination conflict;
- permission error;
- Git command failure;
- partially initialized checkout.

Do not leave a Project silently marked as ready if its source initialization failed.

Where cleanup is safe and deterministic, remove only artifacts created by the failed initialization attempt.

Do not delete pre-existing user files.

---

## 12. Project Lifecycle Integration

Do not invent a new Project state machine.

Inspect existing statuses:

```text
Created
Analyzing
Specification
Architecture
Planning
Implementation
Testing
Reviewing
HumanReview
Completed
Failed
...
```

Choose the smallest integration point.

The Project must not enter the execution path as "ready" while its required source initialization is incomplete.

If the current lifecycle has no appropriate source-initialization boundary, document the gap and use the least invasive explicit mechanism.

Do not redesign `ProjectTransitions` unless absolutely necessary.

---

## 13. Planner Integration

After source initialization, Planner must be able to inspect the actual Project workspace where appropriate.

The intended progression becomes:

```text
Project
   ->
Source initialized
   ->
Planner Agent
   ->
Plan
   ->
WorkItems
```

Do not duplicate workspace resolution.

Planner continues using its existing execution path.

---

## 14. AgentRun Integration

WorkItem-backed AgentRuns must continue to resolve:

```text
Project.RootPath
```

through `IWorkspaceResolver`.

No new source-specific workspace is allowed.

The Agent must see the same source files that:

```text
ProjectService
AgentOrchestrator
Hermes
VerificationGate
```

consider the Project.

---

## 15. Verification Integration

Verification must operate against the initialized source tree:

```text
Agent
   ->
modify Project.RootPath
   ->
VerificationGate
   ->
dotnet build
   ->
dotnet test
```

Do not introduce a copy of the repository for verification.

Do not alter the existing Factory-owned verification command policy.

---

## 16. Git Security

If Git is introduced, explicitly prevent agent-controlled repository operations from escaping the workspace.

The Agent may modify files in:

```text
Project.RootPath
```

but it must not gain an unrestricted Git capability merely because Git exists in the container.

Do not expose a separate arbitrary shell-to-host bridge.

Use the existing container/process security model.

---

## 17. Authentication / Secrets

Do not implement a complete secrets-management system in this phase.

Inspect existing secret/provider patterns.

If private repository access is unsupported by the current infrastructure:

- implement public repository support first;
- document private-repository authentication as a future capability.

Never hard-code:

```text
username
password
token
SSH key
```

into source code, Project entities, prompts, or tests.

Do not persist repository credentials in `Project`.

---

## 18. API Design

Determine whether source initialization belongs directly in:

```text
POST /api/projects
```

or requires an explicit semantic operation.

Prefer a minimal explicit source initialization operation if initialization can fail independently of Project metadata creation.

Do not add a generic filesystem-upload endpoint.

A successful API operation must have unambiguous semantics.

---

## 19. Tests

Add focused tests for:

### Slug/path safety

Test:

- normal project names;
- spaces;
- punctuation;
- Unicode;
- traversal attempts;
- Windows separators;
- Unix separators;
- absolute-path-like names.

Prove generated RootPath remains inside the intended workspace boundary.

### AllowedRoot

Test that a configured AllowedRoot correctly contains the Factory-generated project workspace regardless of process current directory.

### Workspace initialization

Test:

- empty workspace initialization;
- existing directory;
- non-empty directory;
- repeated initialization;
- failure cleanup;
- invalid source;
- source checkout failure.

### Project creation / lifecycle

Prove a Project cannot become execution-ready while required source initialization has failed.

### Integration

At minimum prove:

```text
Create Project
    ->
Provision workspace
    ->
Initialize source
    ->
Project.RootPath contains expected source file
    ->
Create/dispatch WorkItem
    ->
AgentOrchestrator sees the source
```

Use a deterministic local fixture/repository rather than a real external Git provider for the primary automated test.

---

## 20. Real Repository Integration Test

Add a local deterministic Git fixture if practical.

Preferred:

```text
temporary local bare repository
        ->
clone into Project.RootPath
        ->
verify source
```

This avoids depending on the public internet for core CI tests.

Do not make the main test suite dependent on GitHub availability.

---

## 21. Real Hermes E2E Preparation

Do not make the full real-Hermes test mandatory if Docker/provider access is unavailable.

However, leave the system in a state where the next phase can run:

```text
Create Project
    ->
Provision workspace
    ->
Initialize real source
    ->
Create WorkItem
    ->
AgentRun
    ->
Hermes container mounts Project.RootPath
```

The next phase should be able to test actual source modification rather than starting with an empty directory.

---

## 22. No Scope Creep

Do not:

- redesign Project lifecycle;
- redesign AgentRun;
- redesign Scheduler;
- redesign VerificationGate;
- redesign Repair;
- implement true cancellation/preemption;
- implement all Git providers;
- implement full secret management;
- build repository hosting;
- reintroduce `AutonomousCodingOrchestrator`;
- restore copy-in/copy-out workspace execution.

This phase is:

```text
real Project source
+
safe repository initialization
+
path hardening
```

---

## 23. Migration Policy

Prefer zero migration.

If source metadata must be persisted, use the smallest additive design possible.

Before adding fields, determine whether source information can remain:

- request-scoped;
- configuration-driven;
- represented by an existing Project field.

Do not add a large repository metadata model.

---

## 24. Validation

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

Perform repository-wide searches for:

```text
RootPath =
GenerateSlug
Directory.CreateDirectory
git clone
git checkout
ProcessStartInfo
```

Confirm there is still exactly one authoritative Project workspace path.

Do not fabricate real external repository or Hermes results.

---

## 25. Failure Handling / NO-GO

Return **NO-GO** if:

- Project source can be written outside `Project.RootPath`;
- Project name can still generate an escaping filesystem path;
- configured `AllowedRoot` can be bypassed through relative RootPath resolution;
- repository initialization can overwrite an existing non-empty workspace without explicit policy;
- failed initialization can leave the Project falsely executable;
- Git execution uses unrestricted arbitrary shell input;
- source initialization introduces a second workspace authority;
- Agent and Verification use different source trees;
- implementation recreates legacy copy-in/copy-out execution;
- private credentials are persisted insecurely.

---

## 26. Final Report

Create:

```text
.ai/context/phase-18.16-project-source-initialization.md
```

Document:

- repository/source architecture findings;
- selected source initialization model;
- source abstraction;
- Project creation/lifecycle integration;
- Git behavior, if implemented;
- slug/path hardening;
- AllowedRoot behavior;
- workspace safety;
- failure semantics;
- existing-directory policy;
- security/credential handling;
- tests;
- local repository integration result;
- real Hermes result, if executed;
- schema/migration result;
- remaining gaps.

---

## 27. Definition of Done

A newly initialized real Project must be able to reach:

```text
Project
    |
    v
Provisioned RootPath
    |
    v
Real Source Code
    |
    v
Planner
    |
    v
WorkItems
    |
    v
AgentRun
    |
    v
Hermes
```

The source tree visible to Hermes must be the same physical Project workspace subsequently verified by:

```text
VerificationGate
    ->
dotnet build
    ->
dotnet test
```

The generated Project workspace must remain inside the configured security boundary.

Project names cannot produce path traversal.

Source initialization must be deterministic and safe.

Final decision must be exactly:

```text
Project Source Initialization: VERIFIED
Final Decision: GO
```

or:

```text
Project Source Initialization: NOT VERIFIED
Final Decision: NO-GO
```

Core principle:

> A Project is not merely metadata plus an empty directory.
> A usable Project has a real source tree.
> Source initialization happens inside the Factory-approved workspace.
> Hermes modifies that same source tree.
> Verification validates that same source tree.
> Repository initialization must never become a second execution architecture.