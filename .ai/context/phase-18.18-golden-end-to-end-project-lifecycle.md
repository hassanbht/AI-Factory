# Phase 18.18 — Golden End-to-End Project Lifecycle & Repair

## Mission & Executive Summary

Phase 18.18 establishes the first complete, reproducible, end-to-end acceptance proof of the AI Factory product lifecycle:
from initial project creation to workspace provisioning, Git source initialization, task planning, dependency resolution, controlled task dispatch, real workspace modification, Factory verification (`IVerificationGate`), a mandatory failure and repair loop, review/evaluation/learning recording, project-level gate progression (`Implementation -> Testing -> Reviewing -> HumanReview`), security enforcement against unapproved status transition bypasses, and explicit human approval to `Project.Completed`.

```text
             REAL PROJECT (ProjectService.CreateAsync)
                                |
                                v
             WORKSPACE PROVISIONED (ProjectWorkspaceProvisioner)
                                |
                                v
             REAL GIT SOURCE INITIALIZED (ProjectSourceInitializer)
                                |
                                v
             PLANNING & WORKITEMS WITH REAL DEPENDENCIES
                                |
                                v
             TASK DISPATCH SCHEDULER (MaxConcurrentTasks = 1)
                                |
                                v
             AGENTRUN CREATION & CLAIM (AgentRunWorker)
                                |
                                v
             AGENT EXECUTION & HOST WORKSPACE MODIFICATION
                                |
                                v
             FACTORY VERIFICATION GATE (IVerificationGate / dotnet test)
                                |
                    +-----------+-----------+
                    |                       |
                  PASS                     FAIL
                    |                       |
                    |                       v
                    |               WORKITEM REPAIRING
                    |                       |
                    |                       v
                    |              REPAIR AGENT DISPATCH
                    |                       |
                    |                       v
                    |               REPAIR MODIFICATION
                    |                       |
                    +-----------+-----------+
                                |
                                v
                     ALL WORKITEMS COMPLETED
                                |
                                v
             PROJECT-LEVEL GATE EVALUATION (ProjectCompletionGate)
                                |
                                v
             PROJECT TESTING (VerifyProjectAsync on host workspace)
                                |
                                v
             PROJECT REVIEWING
                                |
                                v
             PROJECT HUMAN REVIEW
                                |
                                | [Attempted generic bypass: REJECTED (409)]
                                |
                                | Explicit Human Approval (POST /api/projects/{id}/approve)
                                v
             PROJECT COMPLETED (200 OK)
```

---

## 1. Golden Project Definition

- **Name**: `Golden Demo`
- **Slug**: `golden-demo`
- **RootPath**: `projects/golden-demo` (resolved under `AgentWorkspaceOptions.ProjectsBaseDirectory`)
- **Initial Status**: `ProjectStatus.Created`
- **Target Framework**: `.NET 10.0`
- **Solution Type**: Standard C# Class Library (`src/Demo.csproj`) and xUnit Test Project (`tests/Demo.Tests.csproj`) bound via solution descriptor `Demo.slnx` at repository root.

---

## 2. Source Fixture Details

Generated via `GoldenProjectFixture.CreateBareRepo(tempDir)`:
- Seed directory initialized with Git (`main` branch, deterministic user email/name, GPG signing disabled).
- Initial commit created containing:
  - `Demo.slnx` (declaring `src/Demo.csproj` and `tests/Demo.Tests.csproj`)
  - `src/Demo.csproj`
  - `src/GreetingService.cs` (`public string GetGreeting() => "Hello";`)
  - `tests/Demo.Tests.csproj` (referencing `xunit 2.9.3`, `Microsoft.NET.Test.Sdk 17.11.1`, and `src/Demo.csproj`)
  - `tests/GreetingServiceTests.cs` (verifying `GetGreeting()` is not empty)
- Bare clone created at `golden.git` to serve as the remote repository cloned by `ProjectSourceInitializer`.

---

## 3. WorkItem Graph & Dependency Ordering

Four distinct WorkItems were created and wired with real, non-trivial dependencies:

```text
T01 (Baseline Setup)
    ^
    | depends on
T02 (Implementation Change: GreetingService modification)
    ^
    | depends on
T03 (Test Verification Change: introduces intentional defect -> Repair Loop)
    ^
    | depends on
T04 (Final Integration: Integration method and verification)
```

- **Dependency Enforcement**:
  - `TaskDispatchScheduler` evaluated readiness using `ITaskDependencyResolver`.
  - When `MaxConcurrentTasks = 1`, only T01 was eligible and dispatched first.
  - T02 became ready only after T01 transitioned to `Completed`.
  - T03 became ready only after T02 transitioned to `Completed`.
  - T04 became ready only after T03 completed its repair cycle and transitioned to `Completed`.

---

## 4. AgentDefinitions Required

Bootstrapped via `AgentDefinitionBootstrapper.EnsureBootstrapDefinitionsAsync`:
1. `Planner` (System planner agent for project plan decomposition)
2. `Implementation` (Coding agent for software implementation tasks)
3. `Repair` (Repair agent for diagnosing and fixing verification failures)
4. `Test` (Testing agent for test generation and verification)
5. `Review` (Review agent for independent code review)

All definitions are registered with `Enabled = true`.

---

## 5. Exact Lifecycle Execution

1. **Project Created**:
   `ProjectService.CreateAsync("Golden Demo", ...)` generated `Slug = "golden-demo"`, provisioned directory `workspaces/projects/golden-demo` before persisting, and set `Status = ProjectStatus.Created`.
2. **Git Source Initialized**:
   `ProjectService.InitializeSourceAsync` called `ProjectSourceInitializer.InitializeAsync`, cloned `golden.git`, verified clean revision, and transitioned `ProjectStatus.Created -> ProjectStatus.Analyzing`.
3. **Specification & Architecture**:
   `ProjectService.TransitionStatusAsync` moved project through `Specification` and `Architecture` to `Planning`.
4. **Planning & WorkItem Creation**:
   T01, T02, T03, T04 created; dependencies wired; all items moved to `Ready`.
5. **Project to Implementation**:
   Project moved to `Implementation`.
6. **Task Dispatch & Execution**:
   - Cycle 1: Dispatched T01 (`Queued` AgentRun created). `AgentRunWorker` executed run. `VerificationGate` ran `dotnet test` (passed). `WorkItemCompletionProcessor` recorded reviews/evals. T01 `Completed`.
   - Cycle 2: Dispatched T02. `AgentRunWorker` executed run. Source file `src/GreetingService.cs` updated to `"Hello, AI Factory!"`. `VerificationGate` passed. T02 `Completed`.
   - Cycle 3: Dispatched T03. Initial execution injected intentional defect into `tests/GreetingServiceTests.cs` (asserting `"Hello, Repaired AI Factory!"`). `VerificationGate` ran `dotnet test`, which failed as expected! WorkItem transitioned to `Repairing`, recording `CurrentError`.
   - Cycle 4: Scheduler prioritized `Repairing` WorkItem. Incremented `RepairAttemptCount` from 0 to 1. Dispatched `Repair` AgentRun. Repair agent updated `src/GreetingService.cs` to return `"Hello, Repaired AI Factory!"`. `VerificationGate` ran `dotnet test`, which passed green! `WorkItemCompletionProcessor` processed review/learning. T03 `Completed`.
   - Cycle 5: Dispatched T04. Final integration added `IsIntegrated()` and tests. `VerificationGate` passed. T04 `Completed`.
7. **Project Completion Gate**:
   - `ProjectCompletionGate.EvaluateAsync` detected all 4 WorkItems `Completed`.
   - Advanced `ProjectStatus.Implementation -> ProjectStatus.Testing`.
   - Executed `VerifyProjectAsync` on the host workspace (running `dotnet build` and `dotnet test` across the whole solution). Passed.
   - Advanced `ProjectStatus.Testing -> ProjectStatus.Reviewing`.
   - Advanced `ProjectStatus.Reviewing -> ProjectStatus.HumanReview`.
8. **Security Boundary Enforcement**:
   - Calling `ProjectService.TransitionStatusAsync(projectId, ProjectStatus.Completed)` was rejected with `ConflictException` (409 Conflict).
9. **Explicit Human Approval**:
   - Calling `ProjectService.ApproveAsync(projectId, "qa-lead@factory.local")` succeeded and transitioned project to `ProjectStatus.Completed`.

---

## 6. Real Hermes Environment Status

- **Docker Server**: Docker version 29.7.2 available and running on host.
- **Hermes Docker Image**: `nousresearch/hermes-agent:latest` (3.89GB) present in local Docker cache.
- **OmniRoute / Provider Gateway**: Port 20128 local gateway container not running in test environment.
- **`AI_FACTORY_HERMES_SMOKE_TEST`**: Unset / disabled by default (opt-in only convention).

---

## 7. Real Hermes Execution Result

- **REAL HERMES**: `NOT RUN`
  - **Reason**: `AI_FACTORY_HERMES_SMOKE_TEST` environment variable was unset, and local OmniRoute proxy (`http://host.docker.internal:20128/v1`) was unavailable during this run.
  - **Real Hermes Test Implementation**: Test methods `RealHermes_ExecutesWorkItem_AgainstInitializedProjectWorkspace_AndVerificationGatePasses` and `RealHermes_FullProjectLifecycle_WithRepair_WhenEnabled` exist in `RealHermesE2ETests.cs` and are wired to execute real containerized Hermes runs when opt-in is enabled.

---

## 8. Actual Modified Source Files

All files in the host Project workspace (`projects/golden-demo/`) were verified on disk:
- `src/GreetingService.cs`:
  ```csharp
  namespace Demo;

  public class GreetingService
  {
      public string GetGreeting() => "Hello, Repaired AI Factory!";
      public bool IsIntegrated() => true;
  }
  ```
- `tests/GreetingServiceTests.cs`:
  ```csharp
  using Xunit;

  namespace Demo.Tests;

  public class GreetingServiceTests
  {
      [Fact]
      public void Greeting_MatchesExpectedGold()
      {
          var service = new GreetingService();
          Assert.Equal("Hello, Repaired AI Factory!", service.GetGreeting());
      }

      [Fact]
      public void Service_IsIntegrated()
      {
          var service = new GreetingService();
          Assert.True(service.IsIntegrated());
      }
  }
  ```

---

## 9. Verification Result

- **WorkItem Verification**: `IVerificationGate.VerifyAsync` executed real `dotnet build` and `dotnet test` against `projects/golden-demo/` during each WorkItem cycle.
- **Project Verification**: `IVerificationGate.VerifyProjectAsync` executed real `dotnet build` and `dotnet test` against the root workspace during the project-level testing phase.
- **Independent Verification**: Test suite ran a standalone host-level `dotnet test --no-restore -c Debug` against the finalized workspace directory, confirming 100% test success on the actual disk files.

---

## 10. Repair Result

- **Deliberate Failure Triggered**: T03 introduced an assertion expecting `"Hello, Repaired AI Factory!"` when the service returned `"Hello, AI Factory!"`.
- **Failure Classification**: `VerificationGate` returned `VerificationStatus.Failed` with build/test failure output.
- **State Transition**: `AgentRunWorkItemSync` transitioned WorkItem to `WorkItemStatus.Repairing`.
- **Repair Dispatch**: `TaskDispatchScheduler` prioritized the repairing item, incremented `RepairAttemptCount = 1`, and dispatched a `Repair` agent run.
- **Repair Resolution**: Source code was repaired, subsequent `VerificationGate` passed, and WorkItem transitioned to `Completed`.

---

## 11. Review / Evaluation / Learning Result

- `WorkItemCompletionProcessor` ran on every successful WorkItem verification pass.
- `ValidationRun` persisted with build/test outputs.
- `ReviewRun` persisted with structured findings.
- `EvaluationRun` persisted with evaluation metrics.
- `LearningStore` updated with execution lessons.

---

## 12. Project Gate Result

- `ProjectCompletionGate.EvaluateAsync` automatically transitioned:
  - `Implementation` -> `Testing` (once all 4 WorkItems completed).
  - `Testing` -> `Reviewing` (once project-level verification passed).
  - `Reviewing` -> `HumanReview` (defensive check passed).

---

## 13. HumanReview Result

Project entered `ProjectStatus.HumanReview` and paused awaiting human intervention.

---

## 14. Approval Result

- Attempting to call generic transition to `Completed` threw `ConflictException` with message:
  `"Project completion requires explicit human approval — use the project approval action instead of the generic status endpoint."`
- Calling `ProjectService.ApproveAsync(projectId, "qa-lead@factory.local")` succeeded and emitted `AuditEventType.HumanApproval`.

---

## 15. Final Project Status

- `Project.Status`: **`Completed`**

---

## 16. Execution Trace Identifiers

| Stage | Identifier | Result |
|---|---|---|
| Project | `projectId` (`Golden Demo`) | `Completed` |
| WorkItem T01 | `t01Id` (`T01 - Setup`) | `Completed` |
| WorkItem T02 | `t02Id` (`T02 - Implementation`) | `Completed` |
| WorkItem T03 | `t03Id` (`T03 - Test Verification`) | `Completed` (Repair Count: 1) |
| WorkItem T04 | `t04Id` (`T04 - Integration`) | `Completed` |
| Verification Gate | `VerificationGate` | `Passed` (All cycles) |
| Project Gate | `ProjectCompletionGate` | Advanced through `HumanReview` |
| Human Approval | `qa-lead@factory.local` | Approved |

---

## 17. Deterministic Test Results

- `GoldenProjectLifecycleE2ETests`:
  - Result: **1 passed, 0 failed, 0 skipped** (Execution time: 54s)
- `AI.Factory.Worker.Tests` (non-live suite):
  - Result: **76 passed, 0 failed, 0 skipped** (Execution time: 56s)

---

## 18. Full Build / Test Results

- **Debug Solution Build**: `dotnet build apps/AI.Factory.slnx -c Debug` -> **0 warnings, 0 errors**
- **Release Solution Build**: `dotnet build apps/AI.Factory.slnx -c Release` -> **0 warnings, 0 errors**
- **Application Tests**: `dotnet test apps/tests/AI.Factory.Application.Tests` -> **791 passed, 0 failed, 0 skipped**
- **API Tests**: `dotnet test apps/tests/AI.Factory.Api.Tests` -> **302 passed, 0 failed, 0 skipped**
- **Worker Tests (non-live)**: `dotnet test apps/tests/AI.Factory.Worker.Tests` -> **76 passed, 0 failed, 0 skipped**
- **Total Automated Tests**: **1,169 passed, 0 failed, 0 skipped**

---

## 19. Migration Status

- Command: `dotnet ef migrations has-pending-model-changes --startup-project apps/src/AI.Factory.Api --project apps/src/AI.Factory.Infrastructure`
- Result: **No changes have been made to the model since the last migration.** (Zero migrations required).

---

## 20. Remaining Blockers for Production

1. **Provider Gateway Orchestration**:
   - While Docker engine is available, real containerized Hermes execution requires an accessible, configured OpenAI-compatible completion endpoint (e.g. OmniRoute proxy or active OpenRouter API key) in the runtime environment.
2. **Multi-Project Concurrency**:
   - `TaskDispatchScheduler` is currently bounded by `MaxConcurrentTasks = 1` for predictable serial execution; multi-task parallel dispatch within the same project requires workspace branch/merge isolation.

---

## Final Decision

```text
ARCHITECTURE VERIFIED:       YES
DETERMINISTIC E2E VERIFIED:  YES
REAL HERMES INFRASTRUCTURE:  PREPARED (OPT-IN READY)

Golden Project Lifecycle: VERIFIED
Final Decision: GO
```
