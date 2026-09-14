# Phase 18.14 — Deterministic Agent Workspace & Execution Context

## Phase Objective

Close the remaining execution-context gap in the unified WorkItem execution pipeline.

Phase 18.13 successfully unified WorkItem execution around:

```text
WorkItem
   -> AgentRun
   -> AgentRunWorker
   -> AgentOrchestrator
   -> IAgentRuntime / Hermes
   -> StructuredAgentResult
   -> Verification
   -> Repair
```

However, Phase 18.13 explicitly identified that the new runtime still executes with:

```text
WorkingDirectory = string.Empty
```

and that the former legacy execution path had workspace/task-preparation behavior that is no longer present.

This is now the primary execution-integrity gap.

The objective of this phase is to define and implement a **deterministic, Factory-owned Agent workspace/execution context** so every AgentRun has an explicit, safe, reproducible working directory.

Do not reintroduce the deleted legacy execution pipeline.

---

## 1. Repository Inspection

Before changing code, inspect:

- `AGENTS.md`
- Phase 18.3 through 18.13 reports
- `AgentRun`
- `AgentOrchestrator`
- `AgentRunWorker`
- `IAgentRuntime`
- `HermesAgentRuntime`
- `IAgentExecutor`
- Hermes execution configuration
- `Project.RootPath`
- `WorkItem`
- workspace/path helpers
- `WorkspacePathGuard`
- artifact infrastructure
- `IArtifactRepository`
- `ReadArtifactTool`
- `TaskDispatchScheduler`
- `OrchestrationRun`
- `OrchestrationService`
- Docker/process execution code
- `IProcessRunner`
- `CommandPolicyOptions`
- `ExecutionLimitsOptions`
- all relevant integration tests

Search the repository for:

```text
WorkingDirectory
RootPath
Workspace
WorkspacePathGuard
IAgentExecutor
HermesAgentRuntime
ProcessStartInfo
UseShellExecute
OrchestrationRun
AgentRun
```

Determine exactly where the Agent's working directory is currently decided and where filesystem access is currently permitted.

---

## 2. Target Execution Model

Every WorkItem-backed AgentRun must have a deterministic execution context:

```text
Project.RootPath
      |
      v
Factory validates / resolves workspace
      |
      v
AgentRun execution context
      |
      v
Hermes WorkingDirectory
```

The Agent must never receive:

```text
string.Empty
```

as an accidental/default working directory for a WorkItem execution.

The workspace must be derived from Factory-owned state, not from arbitrary LLM output.

---

## 3. Source of Truth

Prefer:

```text
Project.RootPath
```

as the authoritative base workspace when it represents an existing valid project workspace.

Do not allow an Agent to choose its own execution root.

Do not accept a filesystem path from:

- `StructuredAgentResult`;
- agent output;
- planner output;
- LLM-generated commands;
- arbitrary API input.

If the current project model does not guarantee that `RootPath` is always a safe/existing directory, establish a deterministic validation/resolution layer rather than silently assuming it.

---

## 4. Workspace Resolution Contract

Introduce a small abstraction only if the existing repository has no suitable equivalent.

Preferred conceptual contract:

```csharp
IWorkspaceResolver
```

or equivalent.

Responsibilities should be limited to:

- resolve the workspace for a Project/WorkItem;
- validate that the path is permitted;
- normalize it;
- prevent path traversal / escaping Factory-owned roots;
- provide a deterministic result to the execution runtime.

Do NOT turn the resolver into:

- an execution engine;
- a shell wrapper;
- a Git manager;
- a build system;
- a project planner.

Keep the abstraction narrow.

---

## 5. Security / Path Safety

Reuse:

```text
WorkspacePathGuard
```

or equivalent existing path-safety infrastructure wherever possible.

The implementation must prevent:

```text
../
..\ 
absolute paths outside allowed roots
symlink/path escape where the existing security model requires protection
```

Do not invent a second path-validation algorithm if the repository already has one.

The final workspace must be a trusted Factory-resolved path.

---

## 6. AgentRuntime Integration

Modify the execution path so that:

```text
AgentRun
    ->
AgentOrchestrator
    ->
IAgentRuntime.ExecuteAsync(...)
    ->
HermesAgentRuntime
    ->
IAgentExecutor
```

receives the resolved workspace.

The important architectural constraint is:

> Workspace resolution belongs to the Factory execution layer; Hermes merely receives the already-resolved execution context.

Do not make Hermes responsible for deciding Project paths.

Do not add Project/WorkItem business logic to `HermesAgentRuntime` if it can be injected through the existing execution instruction/context model.

Prefer extending an existing execution-context/instruction abstraction rather than adding ad-hoc path parameters through unrelated layers.

---

## 7. Deterministic Execution Context

Define the minimum required context for an AgentRun.

At minimum consider:

```text
ProjectId
WorkItemId
ProjectRootPath / WorkingDirectory
AgentRunId
```

Only persist additional data if it is genuinely required for restart/recovery/debugging.

Do not add schema fields merely for convenience.

Prefer deriving information from existing IDs when possible.

---

## 8. Restart / Recovery Semantics

Workspace resolution must be deterministic after:

- Worker restart;
- AgentRun retry;
- repair dispatch;
- process restart;
- manual `/api/orchestration-runs` execution.

No execution should depend on process-local memory containing the previous workspace.

A recovered AgentRun must resolve the same workspace from persisted Factory state.

---

## 9. Manual OrchestrationRun Compatibility

Phase 18.13 retained `/api/orchestration-runs` as a compatibility facade.

That path must use exactly the same workspace resolution as normal scheduler execution.

Desired behavior:

```text
/api/orchestration-runs
        |
        v
AgentRun
        |
        v
same WorkspaceResolver
        |
        v
same Hermes execution context
```

There must not be a special legacy workspace implementation for manual runs.

---

## 10. Verification / Build / Repair

Do not change the existing verification or repair architecture.

The workspace used by:

```text
Agent execution
VerificationGate
BuildExecutor
TestExecutor
Repair execution
```

must be consistent where the domain requires the same Project root.

Do not accidentally make verification trust an Agent-supplied directory.

Factory-owned `Project.RootPath` remains authoritative for:

```text
build
test
project verification
```

Agent execution may use the same resolved workspace, but execution-specific subdirectories are allowed only if an existing design explicitly requires them.

---

## 11. Workspace Existence Policy

Determine empirically what should happen when:

```text
Project.RootPath
```

is:

- null;
- empty;
- malformed;
- nonexistent;
- inaccessible;
- outside the allowed workspace root.

Choose a deterministic policy.

Preferred:

```text
invalid workspace
    ->
AgentRun fails deterministically
    ->
WorkItem enters the existing failure path
```

Do not silently substitute:

```text
current directory
temp directory
repository root
```

without an explicit repository-supported reason.

Do not let an invalid workspace become an implicit host/container working directory.

---

## 12. Container / Host Behavior

Inspect how Hermes actually executes in:

- local development;
- Worker process;
- Docker container.

Ensure that the resolved path is valid in the execution environment.

If host/container path translation is already present, reuse it.

If it is absent, design the smallest explicit mapping mechanism required.

Do not hard-code Windows-only or Linux-only assumptions.

The implementation must not work only on the developer workstation.

Add deterministic tests for the execution environment assumptions that can be tested without a real Hermes instance.

---

## 13. Workspace Preparation

Phase 18.13 identified that legacy workspace/task-preparation behavior disappeared during unification.

Do NOT blindly restore the old `AutonomousCodingOrchestrator` preparation implementation.

First determine exactly what the old preparation actually did:

- directory creation;
- source checkout/copy;
- artifact placement;
- task materialization;
- environment setup.

Classify each behavior as:

```text
A. Still required
B. Already provided elsewhere
C. Obsolete
D. Future scope
```

Only implement behavior classified as genuinely required for the new AgentRun pipeline.

The workspace mechanism must belong to the new architecture, not recreate the old orchestrator.

---

## 14. Workspace Lifecycle

Do not introduce unnecessary workspace cleanup/deletion behavior.

Determine whether the Project root represents:

```text
persistent project workspace
```

or:

```text
temporary execution workspace
```

based on the current repository.

If persistent, never automatically delete it.

If temporary execution workspaces are genuinely required, isolate that concern behind a dedicated abstraction and make cleanup deterministic and crash-safe.

Do not make workspace lifecycle depend on an Agent's success/failure report.

---

## 15. Tests

Do not rerun the complete Phase 18.13 suite simply to repeat old coverage.

Add focused tests for the new execution-context behavior.

### Workspace resolution

Test:

- valid Project.RootPath;
- invalid path;
- empty path;
- path escaping allowed root;
- deterministic normalization;
- same Project resolves same workspace.

### Agent runtime integration

Test:

```text
AgentRun
 -> AgentOrchestrator
 -> IAgentRuntime
```

receives the expected workspace.

Use a fake runtime/executor.

Do not require real Hermes for deterministic unit tests.

### Manual execution

Prove `/api/orchestration-runs` uses the exact same workspace resolution.

### Repair

Prove a repair AgentRun receives the same correct Project workspace.

### Recovery

Simulate loading an existing AgentRun in a new scope/process and verify workspace resolution does not rely on in-memory state.

### Invalid workspace

Prove an invalid workspace produces a deterministic failure and does not silently execute in the application working directory.

### Path security

Prove path traversal/outside-root attempts are rejected.

---

## 16. Real Hermes E2E

Add or extend a focused real-Hermes E2E test only if the repository's existing opt-in mechanism is available.

The test should prove:

```text
real AgentRun
    ->
Hermes
    ->
real WorkingDirectory
    ->
agent can observe expected project workspace
```

Do not use the real LLM to decide or generate the workspace path.

Do not make real-Hermes execution mandatory if the environment is not configured.

Never fabricate the result.

---

## 17. No Scope Creep

Do not:

- redesign AgentRun;
- add true AgentRun cancellation;
- redesign Project lifecycle;
- redesign TaskDispatchScheduler;
- redesign WorkItem verification;
- redesign repair logic;
- rebuild `AutonomousCodingOrchestrator`;
- reintroduce a second executor;
- implement full Git management;
- implement a new repository/checkout service unless current workspace preparation proves it is required;
- remove `HermesExecution` yet;
- clean up Trace/Progress services yet.

Those can be separate subsequent phases.

This phase is specifically about:

```text
deterministic workspace
+
execution context
```

---

## 18. Migration Policy

Prefer zero migration.

Persist no new workspace field if:

```text
Project.RootPath
```

already provides sufficient authoritative state.

If a persisted `AgentRun` execution-context field is genuinely required for restart correctness, justify it explicitly and prefer:

- nullable;
- additive;
- backward-compatible.

Do not introduce schema changes merely to cache a value that can be deterministically resolved from existing Project/WorkItem state.

---

## 19. Validation

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

Perform a repository-wide search confirming that WorkItem-backed Agent execution no longer deliberately passes:

```text
WorkingDirectory = string.Empty
```

or equivalent empty-directory behavior.

Document all remaining legitimate empty-directory cases, if any.

---

## 20. Failure Handling / NO-GO

Return **NO-GO** if:

- Agent execution can still silently use an implicit current directory;
- workspace can be supplied by the LLM/agent;
- manual and scheduler execution resolve workspaces differently;
- repair execution uses a different workspace without explicit justification;
- invalid Project.RootPath silently falls back to an unsafe directory;
- path traversal can escape the allowed workspace;
- the implementation recreates a second legacy execution engine;
- workspace state depends on process-local memory and is lost after restart;
- real execution still reaches Hermes with an empty working directory for WorkItem-backed runs.

---

## 21. Final Report

Create:

```text
.ai/context/phase-18.14-agent-workspace-execution-context.md
```

Document:

- current execution-context findings;
- old workspace-preparation behavior;
- responsibility classification;
- final workspace authority;
- workspace resolver design;
- path-security behavior;
- AgentRuntime integration;
- manual API behavior;
- repair behavior;
- restart/recovery behavior;
- container/host assumptions;
- workspace preparation decisions;
- schema result;
- focused test results;
- full build/test results;
- real Hermes result, if enabled;
- remaining gaps.

---

## 22. Definition of Done

The final execution architecture must satisfy:

```text
                  Project
                     |
                     v
              Project.RootPath
                     |
                     v
            Workspace Resolution
                     |
                     v
                  AgentRun
                     |
                     v
              AgentOrchestrator
                     |
                     v
               IAgentRuntime
                     |
                     v
            Hermes / AgentExecutor
                     |
                     v
             Trusted WorkingDirectory
```

The same resolution must be used for:

```text
Scheduler execution
Manual execution
Repair execution
Recovered execution
```

No LLM-controlled path selection exists.

No implicit application-current-directory execution exists for WorkItem-backed AgentRuns.

Factory owns workspace authority.

Hermes consumes the resolved workspace.

Final decision must be exactly:

```text
Agent Workspace & Execution Context: VERIFIED
Final Decision: GO
```

or:

```text
Agent Workspace & Execution Context: NOT VERIFIED
Final Decision: NO-GO
```

Core principle:

> The Factory decides where an Agent works.
> The Agent decides what work to perform.
> Hermes executes inside the Factory-approved workspace.
> Verification remains Factory-owned.
> Workspace resolution must be deterministic across scheduler, manual, repair, and recovery paths.