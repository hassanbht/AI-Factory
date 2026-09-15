# Phase 18.17 — Unified Workspace Context & Real Hermes WorkItem E2E

## Phase Objective

Close the last workspace-resolution inconsistency and perform the first real end-to-end WorkItem execution through Hermes against an initialized Project source tree.

Phase 18.16 established:

```text
Project creation
    ->
workspace provisioning
    ->
Git source initialization
    ->
Project.RootPath
    ->
IWorkspaceResolver
    ->
Agent execution
    ->
Verification
```

and confirmed:

- one authoritative Project workspace;
- safe slug generation;
- optional `ProjectsBaseDirectory`;
- optional `AllowedRoot`;
- Git source initialization;
- no legacy copy-in/copy-out execution.

However, Phase 18.16 identified one remaining production code path:

```text
LocalContextProvider
    ->
Path.GetFullPath(project.RootPath)
```

which bypasses `IWorkspaceResolver`.

Also, the most important practical validation is still missing:

```text
real Project source
    ->
real AgentRun
    ->
real Hermes Docker container
    ->
real mounted Project.RootPath
    ->
real agent modification
    ->
Factory VerificationGate
    ->
build/test
```

This phase must address both.

The final objective is to prove the unified runtime works against a real source tree, not only fake runtimes.

---

## 1. Repository Inspection

Before coding, inspect:

- `AGENTS.md`
- Phase 18.14, 18.15 and 18.16 reports
- `IWorkspaceResolver`
- `ProjectWorkspaceResolver`
- `WorkspacePathResolution`
- `WorkspacePathGuard`
- `AgentWorkspaceOptions`
- `ProjectService`
- `ProjectWorkspaceProvisioner`
- `ProjectSourceInitializer`
- `AgentOrchestrator`
- `HermesAgentRuntime`
- `HermesAgentExecutor`
- `LocalContextProvider`
- `IContextProvider`
- `ReviewContextBuilder`
- Planner-related context usage
- `WorkItemCompletionProcessor`
- `IArtifactRepository`
- `FileSystemArtifactRecorder`
- `VerificationGate`
- `TaskDispatchScheduler`
- `AgentRunWorker`
- existing Real Hermes E2E tests
- existing Docker/Hermes configuration
- existing OmniRoute/OpenRouter configuration
- all environment-variable gating around real Hermes tests

Search globally for:

```text
Path.GetFullPath(project.RootPath)
Path.GetFullPath(
project.RootPath
WorkingDirectory
IContextProvider
LocalContextProvider
WorkspacePathResolution
IWorkspaceResolver
AI_FACTORY_HERMES_SMOKE_TEST
```

Identify every production runtime path that resolves or consumes a Project workspace.

---

## 2. Workspace Authority Must Become Truly Global

After this phase, there must be exactly one semantic workspace-resolution authority for Project-backed execution.

The desired rule is:

```text
Project.RootPath
        |
        v
IWorkspaceResolver
        |
        v
resolved absolute workspace
```

This must govern:

```text
Agent execution
Verification
Workspace provisioning validation
Source initialization
Context loading
Review context
Artifact-related workspace access
```

where those components operate on the Project workspace.

Do not leave a second "almost identical" resolver hidden inside Context infrastructure.

---

## 3. Fix LocalContextProvider

Replace the direct Project.RootPath resolution in:

```text
LocalContextProvider
```

with the existing:

```text
IWorkspaceResolver
```

or a lower-level shared abstraction only if the repository architecture requires it.

Requirements:

- no duplicate `Path.GetFullPath(project.RootPath)` semantics;
- no ambient CWD dependency outside the shared resolver;
- honor `ProjectsBaseDirectory`;
- honor `AllowedRoot`;
- fail deterministically if the workspace cannot be resolved;
- do not silently fallback to another directory.

Do not redesign the whole Context system.

The change should be narrowly focused on using the authoritative workspace.

---

## 4. Context Behavior

Determine exactly what `LocalContextProvider` needs from the workspace.

Preserve all existing behavior that is unrelated to path resolution.

Do not change:

- context ranking;
- lexical search semantics;
- artifact search semantics;
- memory semantics;
- planner prompt semantics;

unless necessary to pass the resolved workspace.

The expected pattern is:

```text
IContextProvider
      |
      v
IWorkspaceResolver
      |
      v
Project workspace
      |
      v
existing context logic
```

---

## 5. Context / Agent / Verification Consistency

Add one focused integration test proving that:

```text
LocalContextProvider
AgentOrchestrator
VerificationGate
```

all resolve the same Project to the same absolute physical directory when:

```text
ProjectsBaseDirectory
```

is configured.

Do not depend on process CWD.

Do not duplicate the entire existing test matrices from Phases 18.14–18.16.

---

# REAL HERMES E2E

## 6. Enable the First Real WorkItem E2E

The existing real-Hermes infrastructure is already present.

Do not create a parallel test harness unless the existing one is genuinely incapable of exercising a WorkItem-backed AgentRun.

The real E2E must be gated behind the existing:

```text
AI_FACTORY_HERMES_SMOKE_TEST=true
```

or the repository's actual existing real-Hermes enablement mechanism.

No external network access should be required by the deterministic test suite.

---

## 7. Real E2E Project Setup

Create the smallest possible real Project fixture.

The test must:

1. create a Project through the real `ProjectService`;
2. allow Phase 18.15 provisioning to create the workspace;
3. initialize source through the real Phase 18.16 Git source mechanism;
4. create a real WorkItem;
5. dispatch it through the real `TaskDispatchScheduler`;
6. allow the real `AgentRunWorker` / `AgentOrchestrator` path to execute;
7. invoke the real `HermesAgentRuntime`;
8. invoke the real Docker-backed `HermesAgentExecutor`.

Do not manually set:

```text
Project.RootPath
AgentRun.WorkspacePath
WorkItem status
```

except where unavoidable for test setup.

The purpose is to exercise the real production path.

---

## 8. Deterministic Test Repository

Use a local Git fixture whenever possible.

The repository should contain a deliberately tiny project with one deterministic source file.

For example:

```text
src/
    Demo.csproj
    Program.cs
tests/
    Demo.Tests.csproj
    ProgramTests.cs
```

The fixture should be:

- small;
- deterministic;
- buildable;
- testable;
- isolated;
- free of external dependencies if possible.

Do not depend on GitHub or another internet repository for the primary E2E.

---

## 9. Controlled Agent Objective

The real Hermes Agent must receive a narrowly bounded task.

Example objective conceptually:

```text
Modify the existing Demo project so that the requested behavior is implemented.
Do not modify files outside the project workspace.
Run appropriate local checks.
Return a structured execution result.
```

The actual task should be chosen so that the change can be verified deterministically.

Do not ask the agent to perform arbitrary broad refactoring.

Do not depend on subjective code quality for test success.

---

## 10. File Modification Marker

The E2E must prove that the Agent changed the actual mounted workspace.

Prefer a deterministic source change with an independently verifiable outcome.

For example:

```text
before:
return "Hello";

after:
return "Hello, AI Factory!";
```

or another small behavior change with a test that verifies the new behavior.

The important invariant is:

```text
host Project.RootPath
    == Docker-mounted workspace
    == Hermes working directory
    == VerificationGate workspace
```

---

## 11. Do Not Trust Agent Claims

The real E2E must not pass merely because:

```text
StructuredAgentResult.execution.status == completed
```

The existing Factory-owned VerificationGate remains authoritative.

Required sequence:

```text
Agent execution
    ->
Agent result
    ->
Factory verification
    ->
build
    ->
test
    ->
WorkItem.Completed
```

If the Agent claims success but the source was not actually changed or does not compile/test, the test must fail.

---

## 12. Verify Physical Workspace Identity

The E2E should explicitly prove that the Agent and Verification operate on the same physical source tree.

Use an independently known marker/file/content.

For example:

1. fixture contains a known source value;
2. Agent changes that value;
3. after Hermes exits, host-side Project.RootPath is inspected;
4. changed content is confirmed;
5. VerificationGate builds/tests that same workspace;
6. final WorkItem becomes `Completed`.

Do not infer this identity merely from matching path strings.

The test must prove the actual file content changed in the host Project workspace.

---

## 13. Real Hermes Container Assertions

Where practical, capture evidence that the container actually saw the expected Project source.

Do not rely solely on:

```text
docker run succeeded
```

The E2E should prove that:

```text
/workspace
```

inside the container corresponds to the intended Project workspace and contains the expected fixture files.

Use the least invasive deterministic observation mechanism already supported by the existing Hermes execution design.

Do not expose arbitrary host filesystem inspection to the Agent.

---

## 14. Structured Result

Verify the real Hermes result still reaches:

```text
StructuredAgentResult
```

and that WorkItem state handling continues through the existing pipeline.

Do not create a new result parser for this phase.

The expected flow remains:

```text
Hermes raw output
    ->
IAgentRuntime
    ->
StructuredAgentResult
    ->
AgentRunWorkItemSync
    ->
Verification
```

---

## 15. Completion Processor

Because Phase 18.13 moved Review/Evaluation/Learning into:

```text
WorkItemCompletionProcessor
```

the real E2E should observe the normal completion behavior.

At minimum verify that a successful verified WorkItem reaches the expected terminal state through the current processor.

Do not duplicate review/evaluation/learning logic in the E2E harness.

---

## 16. Repair E2E

Do NOT make real Repair the primary success criterion of this phase.

The primary criterion is proving:

```text
real WorkItem
-> real Hermes
-> real workspace modification
-> real verification
-> Completed
```

A real Repair E2E may be added only if it is straightforward and deterministic with the same fixture.

Otherwise leave it as the next focused E2E phase.

---

## 17. Planner

Planner-only AgentRuns remain allowed to use their existing scratch behavior.

Do not change Planner workspace semantics merely because WorkItem-backed AgentRuns now use Project.RootPath.

However, verify that Planner's context loading does not bypass `IWorkspaceResolver` after the LocalContextProvider change.

Do not turn this phase into Planner redesign.

---

## 18. Real Hermes Environment

Document exactly what must exist on the machine where this E2E is executed.

At minimum identify:

```text
Docker daemon
Hermes image / executable
OmniRoute/OpenRouter-compatible endpoint
API key/credential
AI_FACTORY_HERMES_SMOKE_TEST=true
```

Use the repository's actual configuration names discovered during inspection.

Do not hard-code secrets.

Do not commit API keys.

---

## 19. Failure Diagnostics

When real Hermes E2E fails, diagnostics must clearly distinguish:

```text
Project creation failure
Workspace provisioning failure
Git initialization failure
AgentDefinition failure
AgentRun dispatch failure
Hermes startup failure
Docker mount failure
LLM/provider failure
Structured result parsing failure
Verification failure
WorkItem completion failure
```

Do not collapse all failures into "Hermes failed".

Use the existing audit/logging mechanisms.

---

## 20. Real E2E Test Gating

The test must be skipped cleanly when:

```text
AI_FACTORY_HERMES_SMOKE_TEST
```

is not enabled or required infrastructure is unavailable.

Do not make normal CI dependent on:

- Docker;
- live LLM provider;
- network connectivity;
- personal credentials.

The normal deterministic suite must remain green without the real E2E.

---

## 21. Concurrency

Do not increase per-project concurrency for this test.

Use the existing default:

```text
MaxConcurrentTasks = 1
```

to avoid simultaneous writes to the same live Project workspace.

Do not introduce locks or a new isolation mechanism.

---

## 22. Security

The real E2E must preserve:

```text
Docker --network none
```

where the current Hermes executor enforces it.

Do not weaken container isolation merely to make the test pass.

The Agent receives:

```text
Project.RootPath
```

through Factory-controlled execution context only.

The Agent does not receive:

```text
AllowedRoot
ProjectsBaseDirectory
host filesystem root
```

as mutable inputs.

---

## 23. Required Tests

Add focused deterministic tests for:

### Workspace unification

- LocalContextProvider uses IWorkspaceResolver.
- ProjectsBaseDirectory is honored.
- AllowedRoot is honored.
- changing CWD does not alter configured-base resolution.
- Context/Agent/Verification resolve the same workspace.

### Real Hermes E2E

At minimum one opt-in test:

```text
RealHermes_WorkItemExecution_ModifiesProjectWorkspace_AndPassesFactoryVerification
```

The test must prove:

```text
Project created
    ->
workspace provisioned
    ->
Git source initialized
    ->
WorkItem dispatched
    ->
real AgentRun
    ->
real Hermes
    ->
real file modification
    ->
Factory verification
    ->
WorkItem.Completed
```

Do not add fake success shortcuts.

---

## 24. Validation

Run focused deterministic tests first.

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

Then, on a Docker/provider-enabled environment, run the real E2E explicitly with the repository's actual enablement mechanism, for example:

```powershell
$env:AI_FACTORY_HERMES_SMOKE_TEST="true"
dotnet test apps/AI.Factory.slnx `
  -c Debug `
  --filter "FullyQualifiedName~RealHermes"
```

Use the exact test filter discovered from the actual repository.

Do not claim this test passed unless it actually ran against:

- real Docker;
- real Hermes;
- real provider;
- real Project workspace;
- real WorkItem.

---

## 25. Manual Golden-Path Test

In addition to the automated E2E, create a documented manual scenario that can be executed by the developer.

The manual flow should be:

```text
1. Start OmniRoute/provider configuration
2. Start AI Factory API
3. Start AI Factory Worker
4. Confirm Docker is available
5. Create Project
6. Initialize Git source
7. Confirm Project.RootPath contains the source
8. Create WorkItem
9. Confirm WorkItem becomes Ready
10. Run/enable scheduler
11. Observe AgentRun
12. Observe Hermes container
13. Confirm source file changed
14. Observe VerificationGate build/test
15. Confirm WorkItem.Completed
16. Confirm Project progression eventually reaches HumanReview
17. Call Project approve endpoint
18. Confirm Project.Completed
```

Document the exact API requests or existing commands needed, using the repository's actual API contracts.

Do not invent endpoint payloads that are not implemented.

---

## 26. No Scope Creep

Do not:

- implement private Git authentication;
- add GitHub/GitLab/ADO integrations;
- redesign Project lifecycle;
- add true AgentRun preemption;
- increase project concurrency;
- redesign Planner;
- redesign Context search;
- introduce a second workspace resolver;
- restore `AutonomousCodingOrchestrator`;
- reintroduce scratch-copy execution for WorkItems.

This phase is:

```text
single workspace authority
+
real Hermes WorkItem E2E
```

---

## 27. Migration Policy

Prefer zero migration.

The LocalContextProvider change and E2E harness should require no schema changes.

If migration changes unexpectedly appear, stop and explain exactly why before modifying the model.

---

## 28. Failure Handling / NO-GO

Return **NO-GO** if:

- `LocalContextProvider` still resolves Project.RootPath independently;
- any production Project-backed component bypasses the authoritative workspace resolver without explicit justification;
- real Hermes can run a WorkItem without the actual Project source mounted;
- the host Project.RootPath does not contain the Agent's actual modifications;
- Verification passes against a different directory from the Agent workspace;
- the E2E succeeds only because the Agent result claims `completed`;
- the real Hermes test silently falls back to scratch storage;
- the test result cannot distinguish fake-runtime success from real-Hermes success;
- Docker/network/provider prerequisites were unavailable but the report claims real-Hermes success.

---

## 29. Final Report

Create:

```text
.ai/context/phase-18.17-unified-context-and-real-hermes-e2e.md
```

Document:

- all production workspace-resolution sites;
- LocalContextProvider change;
- final single workspace authority;
- context/Agent/Verification consistency;
- real Hermes environment;
- exact real E2E setup;
- exact Project/source fixture;
- exact WorkItem;
- actual Hermes result;
- proof of real file modification;
- verification result;
- final WorkItem state;
- whether Project reached HumanReview;
- schema status;
- deterministic test results;
- real Hermes test results;
- manual golden-path instructions;
- remaining limitations.

---

## 30. Definition of Done

The most important proof of this phase is:

```text
               REAL PROJECT
                    |
                    v
             REAL GIT SOURCE
                    |
                    v
              REAL WORKSPACE
                    |
                    v
               REAL WORKITEM
                    |
                    v
                 AGENTRUN
                    |
                    v
              REAL HERMES
                    |
                    v
          Docker /workspace mount
                    |
                    v
        REAL source file modification
                    |
                    v
          Host Project.RootPath
                    |
                    v
           FACTORY VERIFICATION
                    |
              +-----+-----+
              |           |
            build        test
              |           |
              +-----+-----+
                    |
                    v
            WorkItem.Completed
```

And the same workspace must be resolved consistently by:

```text
IWorkspaceResolver
LocalContextProvider
AgentOrchestrator
HermesAgentExecutor
VerificationGate
```

The phase is **not VERIFIED** merely because the deterministic tests are green.

It must also have either:

```text
REAL HERMES E2E: PASSED
```

or an explicit, honest statement that the required Docker/provider environment was unavailable and therefore the real E2E remains unverified.

Final decision must be exactly:

```text
Unified Workspace Context & Real Hermes E2E: VERIFIED
Final Decision: GO
```

or:

```text
Unified Workspace Context & Real Hermes E2E: NOT VERIFIED
Final Decision: NO-GO
```

Core principle:

> There must be one workspace resolution authority.
> Context, Agent execution, and Verification must see the same project tree.
> The real Hermes container must modify the real Project workspace.
> Factory verification must validate those actual modifications.
> Fake-runtime tests prove architecture.
> Real-Hermes E2E proves the product actually works.