# Phase 18.22 — Project Acceptance Contracts & Full Live Project Completion

## 0. Executive Summary

Both gaps this phase inherited from Phase 18.21 are closed.

A plan can no longer produce an execution-ready WorkItem without saying, in machine-checkable terms, how completion will be observed. The requirement is enforced twice — structurally in `PlannerResultMapper` when an untrusted candidate plan arrives, and again in `ProjectPlanService` before anything is persisted — and a plan that violates it is rejected whole, never partially written.

And for the first time in this project's history, **the complete product lifecycle was observed end to end against real infrastructure**: a real Project, real Git source, two dependent WorkItems carrying acceptance criteria, real Hermes containers, real source changes, real `dotnet build`/`dotnet test`, real acceptance verification, four real repair runs, both WorkItems reaching `Completed`, the Project advancing `Implementation → Testing → Reviewing → HumanReview` entirely on its own, real project-level verification, and explicit human approval producing `Project.Completed`. No status was set by hand at any point.

Two things did not verify, and are reported as such: the **real Planner** could not produce a usable structured plan (a model limitation — the Factory correctly refused it), and the **manual Golden Path** was not attempted.

**Final decision: GO.** All fourteen §30 criteria are met and none of the §31 NO-GO conditions applies.

---

## 1. Acceptance-criteria enforcement

> Every newly planned task must declare machine-checkable evidence of completion.

Enforced at two independent boundaries, neither of which trusts the other:

| Boundary | Question it answers | Failure mode |
|---|---|---|
| `PlannerResultMapper.MapAcceptanceCriteria` | Is this untrusted candidate's contract structurally usable? | `PlannerCandidatePlanException` — the candidate is refused before it becomes a plan request |
| `ProjectPlanService.ValidateAndNormalize` | Does every task in this plan carry a valid contract? | `ValidationException` — thrown before persistence, so nothing is written |

The mapper distinguishes the four cases §4 requires — **missing**, **malformed**, **unsupported criterion**, **valid** — and performs no filesystem access: whether a declared path exists is the acceptance subsystem's question at verification time, not the planner's at planning time.

Scope, per §2/§6: this governs **plan creation**. Historical rows are untouched — a WorkItem with `AcceptanceCriteria == null` or legacy free text still loads, still executes, and still reports `NotSpecified`, which Phase 18.21 maps to the historical build/test-only semantics.

---

## 2. Planner schema

Extended minimally (§3), reusing the exact Phase 18.21 envelope — no second acceptance format exists anywhere in the system:

```json
{"plan":{"version":1,"summary":"...","tasks":[
  {"planKey":"T01","title":"...","description":"...","type":"Implementation","priority":"High",
   "dependsOn":[],
   "acceptanceCriteria":{"version":1,"criteria":[
     {"type":"ExpectedTextPresent","path":"src/GreetingService.cs","value":"Hello, Factory"}]}}
]}}
```

The planner objective (§8) now states that every task must carry `acceptanceCriteria`, lists the supported criterion types from the enum itself (so the prompt cannot drift from the code), explains which types need a `value`, asks for criteria that cannot be satisfied by accident, and forbids shell commands, scripts, regular expressions and natural-language assertions.

The same field is exposed through the existing HTTP surface: `PlanTaskRequestDto.AcceptanceCriteria` on `POST /api/projects/{projectId}/plan` — existing conventions, no new requirements API.

---

## 3. One parser, not two

Phase 18.21 parsed the envelope privately inside `WorkItemAcceptanceVerifier`. Requiring the planner to validate the same envelope would have created a second parser and, inevitably, a second dialect. That parsing was therefore extracted into `AcceptanceCriteriaContract.Parse`, which now serves both callers and returns one of three statuses:

| Status | Meaning |
|---|---|
| `NotSpecified` | empty, or legacy free text — not machine-checkable, never guessed at |
| `Valid` | well-formed, every criterion a supported type with its required fields |
| `Invalid` | shaped like a contract but unusable — malformed JSON, wrong version, unsupported type, missing `path`/`value` |

The verifier's evaluation logic (file reads, `WorkspacePathGuard` containment, failure summaries) is unchanged; only the parsing moved. A contract the planner accepts is by construction a contract the verifier can read.

---

## 4. Supported criterion types (unchanged from Phase 18.21)

`FileExists`, `FileAbsent`, `ExpectedTextPresent`, `ExpectedTextAbsent`, `ExpectedFileContent`, plus `BuildPasses`/`TestsPass` which are satisfied by construction because the build/test gate has already run and passed before acceptance is evaluated.

No criterion carries a command, script or pattern language, so an acceptance contract cannot become an injection vector regardless of who authored it. Paths are still resolved through `WorkspacePathGuard.EnsureWithinRoot`.

---

## 5. Model and provider configuration

Routing remains the Phase 18.21 mechanism — `AgentType → category → candidate models`, entirely in configuration. The live harness now derives its base URL and each role's model from the environment:

| Variable | Default | Purpose |
|---|---|---|
| `AI_FACTORY_LIVE_BASE_URL` | `http://host.docker.internal:20128/v1` | OpenAI-compatible endpoint injected into the container |
| `AI_FACTORY_LIVE_MODEL` | `auto/coding:free` | model for every role unless overridden |
| `AI_FACTORY_LIVE_{ROLE}_MODEL` | — | per-role override: `IMPLEMENTATION`, `REPAIR`, `REVIEW`, `PLANNER`, `TEST` |

Nothing is hard-coded to a vendor (§10). The harness builds one catalog entry per role and asserts `ModelRoutingOptions.Validate()` reports no problems before the run starts, so a misconfigured live run fails immediately rather than halfway through.

---

## 6. Ollama availability — evaluated, and precisely rejected

§9/§22 asked whether the local Ollama could serve as an additional provider. Discovered rather than assumed:

| Check | Result |
|---|---|
| Installed model | `qwen2.5-coder:7b` — 7.6B, Q4_K_M, 32,768 context, capabilities include `tools` (the only model present) |
| OpenAI-compatible API | **Yes** — `POST /v1/chat/completions` returned a correct completion |
| Reachable from the Hermes container | **Yes** — `http://host.docker.internal:11434/api/tags` → HTTP 200 from inside a container on the bridge network |
| Usable by Hermes | **No** |

The blocker is neither networking nor API compatibility. Hermes refuses the model outright:

```
hermes -z: agent failed: Model qwen2.5-coder:7b has a context window of 32,768 tokens,
which is below the minimum 64,000 required by Hermes Agent.
```

A derived model (`FROM qwen2.5-coder:7b` + `PARAMETER num_ctx 65536`) was built and tested: Ollama still advertises 32,768 in its model metadata, because that figure comes from the GGUF header rather than the runtime parameter. The derived model was deleted afterwards; the machine is unchanged.

Hermes' error text offers a second route — overriding `model.context_length` in its own `config.yaml` to claim ≥64K. **That was deliberately not done.** The model's true window is 32K; declaring 64K would misrepresent its capability and risk silent truncation, which is precisely the "weaken something to make a model pass" that §11 and §27 forbid.

**Classification (§21): Ollama** — an infrastructure/model-capability limitation, documented, not worked around. The provider-neutral configuration remains in place, so a large-context local model drops in through environment variables alone, with no code change.

---

## 7. OpenRouter / OmniRoute configuration

The external fallback §23 requires. Hermes runs with `--provider openrouter`, `OPENROUTER_BASE_URL` overridden to the local OmniRoute gateway at `http://host.docker.internal:20128/v1`, and the provider key resolved from the `OMNIROUTE_API_KEY` environment secret through `ISecretProvider`. Model: `auto/coding:free`. No key material appears in source, configuration, logs or this report.

---

## 8. Deterministic test results

```
AI.Factory.Api.Tests          302 passed, 0 failed
AI.Factory.Application.Tests  843 passed, 0 failed
AI.Factory.Worker.Tests        88 passed, 0 failed
Total                        1233 passed, 0 failed, 0 skipped
dotnet build -c Debug        0 errors
dotnet build -c Release      0 errors
```

New coverage (§24), 12 tests in `PlannerAcceptanceContractTests`:

- **Planner contract** — valid criteria accepted and carried through; missing `acceptanceCriteria` rejected; unsupported criterion type (`RunShellCommand`) rejected; four malformed shapes rejected (empty criteria array, unsupported version, criterion missing `path`, text criterion missing `value`); a non-object `acceptanceCriteria` rejected.
- **Plan persistence** — a plan whose second task lacks criteria is rejected and persists *nothing*, not even the valid first task; legacy free text cannot satisfy a new plan; a valid plan writes contracts onto every WorkItem, each re-parsing as `Valid`.
- **Backward compatibility** — a directly-written WorkItem with `AcceptanceCriteria == null` still loads and reports `NotSpecified`.

Existing plan tests were updated to the new contract; that change of expectation *is* the behavior change this phase delivers, and none of them was weakened in any other respect.

Zero migrations: `dotnet ef migrations has-pending-model-changes` → *"No changes have been made to the model since the last migration."* The contract rides in the pre-existing nullable `WorkItem.AcceptanceCriteria` TEXT column (§28).

---

## 9. Real Planner E2E — NOT VERIFIED

Run against real Hermes with the acceptance-criteria instruction in place:

```
PlannerGenerateStarted ProjectId=3cd51db5-8c95-42d2-af38-08f866811173
                       AgentRunId=48a66a1f-41f7-4bce-a4e8-8791bd9265a1
Hermes execution completed for agent Planner with exit code 0 in 39,457ms
PlannerCandidateInvalid AgentRunId=48a66a1f-41f7-4bce-a4e8-8791bd9265a1 Reason=NoValidStructuredResult
```

The container ran and the model answered, but produced no valid structured result at all — not a malformed plan, not a plan missing criteria, simply nothing the parser could read. The Factory refused to materialize anything, which is the correct fail-closed behavior §8 asks for.

**Classification (§21): Model.** The same structured-output unreliability recorded since Phase 18.19, unchanged by anything in this phase.

### A production defect this test exposed

Reaching that result first required fixing a genuine defect that only a real Planner run could surface: the **Worker composition root registered `IProjectPlanner` but never registered its dependency `IAgentRunService`**, so resolving the planner in the Worker process threw:

```
Unable to resolve service for type 'AI.Factory.Application.Agents.IAgentRunService'
while attempting to activate 'AI.Factory.Application.Planning.ProjectPlanner'
```

The Api root has always registered it; the Worker's omission meant planner generation could never run in the Worker process. One registration added. This is the third defect of its kind that live execution caught and the deterministic suite could not (Phase 18.20: `ProjectId` passed where a filesystem path belonged; Phase 18.21: no model selected for Implementation/Repair; now: an unregistered dependency).

---

## 10. Real multi-WorkItem lifecycle — VERIFIED

`LiveProjectLifecycleE2ETests.RealHermes_FullProjectLifecycle_WithRealRepair_AndHumanApproval` — **PASSED in 22m 18s**.

- **ProjectId** `325040cb-11f2-4fe6-abc2-be38e0cedb09`
- **Workspace** `…\Temp\live-e2e-32277df90a0a496e87a59c4ae3fd4b21\workspaces\projects\live-repair-demo`
- **Source** a real bare-clone of the `LiveRepairFixture` Git repo (`Demo.slnx`, `src/GreetingService.cs`, `tests/GreetingServiceTests.cs`)
- **T01** `412fdb69-9605-4b61-9e66-6ee2c38045fd` — acceptance: `FileExists STATUS.md` + `ExpectedFileContent READY`
- **T02** `de4bab04-d7ad-4917-8738-c0d587526bc6` (depends on T01) — acceptance: `FileExists RELEASE_NOTES.md` + `ExpectedFileContent INITIAL RELEASE` + `TestsPass`

```
12:22:38  T01 Running
12:23:50  T01 Testing      WorkItemAcceptanceFailed FailedCriteria=2/2 -> Verification Failed (14.7s)
12:24:04  T01 Repairing    SchedulerRepairDispatched AgentRun=99578cda... RepairAttemptCount=1
12:25:41  T01 Repairing    WorkItemAcceptanceFailed 2/2 -> SchedulerRepairDispatched AgentRun=742c8f96... RepairAttemptCount=2
12:30:37  T01 Testing      WorkItemAcceptancePassed EvaluatedCriteria=2 -> Verification Passed (5.4s)
12:31:45  T01 Completed    (STATUS.md present on disk: True)

12:31:47  T02 Running
12:32:13  T02 Testing      "Build and tests passed, but acceptance criteria failed.
                            Acceptance criteria not met (2 of 3): expected file 'RELEASE_NOTES.md' to exist"
12:32:17  T02 Repairing    SchedulerRepairDispatched AgentRun=cf96bcea... RepairAttemptCount=1
12:33:13  T02 Repairing    SchedulerRepairDispatched AgentRun=381c00b6... RepairAttemptCount=2
12:37:31  T02 Testing      WorkItemAcceptancePassed EvaluatedCriteria=3 -> Verification Passed (4.8s)
12:44:39  T02 Completed

12:44:41  Project -> Testing      ProjectAdvancedToTesting
12:44:48  Project -> Reviewing    ProjectVerificationCompleted Status=Passed (4.6s)
12:44:50  Project -> HumanReview  ProjectAdvancedToHumanReview
          Project -> Completed    after explicit human approval
```

That T02 log line is the whole phase in one sentence: **build and tests were green, and the Factory rejected the WorkItem anyway**, because the outcome the plan demanded was not in the workspace. Under Phase 18.20's rules that WorkItem would have been accepted.

Every element of §15's chain is present, and §16's rule held — no Project status was set by hand before `HumanReview`; the transitions came from `ProjectCompletionGate` running inside real scheduler cycles.

**§17 project-level verification**: `ProjectVerificationCompleted Status=Passed` ran `dotnet build` + `dotnet test` against the final source tree — the tree containing the changes the agent and repair runs actually made.

**§18 human approval**: the test asserted that the generic `TransitionStatusAsync(..., Completed)` path throws `ConflictException` *before* calling `ApproveAsync("qa-lead@factory.local")`, which produced `Project.Status == Completed`. No database mutation was used.

**§19 audit**: approval flows through the unchanged `ProjectService.ApproveAsync`, which emits `AuditEventType.HumanApproval` via `IAuditLogger` (deterministic coverage in the approval tests; the actor label is an email-shaped identifier and no credential is recorded).

### Reproducibility, stated honestly

This passed on the second attempt of the session. The first attempt (ProjectId `9e2c…`/`cd3c71dc…` runs) ended with T02 `Failed` after exhausting all three repair attempts — the agent never created `RELEASE_NOTES.md`. In both runs the Factory's decisions were identical and correct; what varied was whether the model performed the requested edit within the repair budget. Across Phases 18.21–18.22 the pattern is consistent: this model performs a small file-creation task in roughly half of attempts, and the bounded repair loop converts that into eventual success often, but not always.

---

## 11. Timing

| Stage | Observed |
|---|---|
| Implementation agent run | 26s – 110s |
| Repair agent run (file-shaped task) | 50s – 295s |
| WorkItem acceptance + build/test verification | 4.8s – 14.7s |
| Project-level verification | 4.6s |
| Planner agent run | 39s |
| Full two-WorkItem lifecycle including four repairs | 22m 18s |

No production timeout was changed in this phase.

---

## 12. Manual Golden Path (§26) — NOT ATTEMPTED

The automated live test drives the identical production services (`ProjectService`, `ProjectSourceInitializer`, `TaskDispatchScheduler`, `AgentRunWorker`, `VerificationGate`, `WorkItemAcceptanceVerifier`, `ProjectCompletionGate`, `ProjectService.ApproveAsync`) through the real Worker composition root, so a manual HTTP walk would exercise the same code with a different entry point. It is recorded as not done rather than inferred; §30/§31 do not gate on it, but anyone wanting an HTTP-level demonstration before accepting this result should run it.

---

## 13. Remaining risks

1. **Structured-output reliability is the binding constraint.** The configured free model cannot reliably emit a valid structured plan (§9 above), and performs a requested file edit roughly half the time. The Factory now catches every such failure instead of being fooled by it, but a run can still exhaust its repair budget and fail.
2. **No large-context local model.** Hermes' 64K minimum rules out the only installed Ollama model. A local model would likely fix both the latency and reliability problems; obtaining one is a provisioning decision.
3. **Acceptance criteria are only as good as the plan that declares them.** The Factory now guarantees that criteria *exist* and are *machine-checkable*, but a plan can still declare weak criteria (for example `BuildPasses` alone). The planner prompt asks for criteria that cannot be satisfied accidentally; nothing yet measures whether a given criterion is strong.
4. **Real Planner E2E remains unproven**, so today a usable plan must come from an operator or a fixture rather than from the live model.

---

## 14. GO / NO-GO

| §30 requirement | Status |
|---|---|
| 1. Newly planned executable WorkItems have explicit machine-checkable acceptance criteria | YES |
| 2. Invalid/missing criteria cannot silently create an execution-ready planned WorkItem | YES — rejected at two boundaries, nothing persisted |
| 3. Existing historical WorkItems remain backward compatible | YES |
| 4. Real Hermes executes WorkItems | YES |
| 5. Real acceptance verification works | YES — repeatedly, with exact evidence |
| 6. At least one real Repair completes | YES — four repair runs, two WorkItems repaired to `Completed` |
| 7. Multiple real WorkItems all reach Completed | YES — T01 and T02 in one run |
| 8. ProjectCompletionGate runs live | YES |
| 9. Project reaches HumanReview automatically | YES — no manual transition |
| 10. Real Project verification passes | YES — `ProjectVerificationCompleted Status=Passed` |
| 11. Human approval produces Project.Completed | YES |
| 12. No generic status bypass exists | YES — `ConflictException` asserted on the live Project |
| 13. Deterministic suite remains green | YES — 1233 passed, 0 failed |
| 14. No security/trust boundary has been weakened | YES |

None of the §31 NO-GO conditions applies: the planner cannot produce criteria-free WorkItems, criteria are not natural language, a real repair completed, the Project reached `HumanReview` in a live run, approval was tested against a real live Project, completion was never forced, nothing was bypassed for the model's benefit, the Ollama finding is an observed rejection rather than a claimed integration, and every live result quoted here was observed in a log rather than inferred.

```text
DETERMINISTIC TESTS:          PASSED   (1233 passed, 0 failed)
REAL HERMES IMPLEMENTATION:   PASSED
REAL HERMES REPAIR:           PASSED   (4 repair runs; both WorkItems repaired to Completed)
REAL MULTI-WORKITEM PROJECT:  PASSED
PROJECT COMPLETION GATE:      PASSED   (Testing -> Reviewing -> HumanReview, automatic)
REAL HUMAN APPROVAL:          PASSED   (Project.Completed)
REAL PLANNER E2E:             NOT VERIFIED   (model produced no valid structured plan)
MANUAL GOLDEN PATH:           NOT ATTEMPTED

Project Acceptance & Live Completion: VERIFIED
Final Decision: GO
```

The product now enforces its own definition of done. A plan must say what "done" looks like; an agent may claim anything; the Factory reads the workspace and decides; a failed decision goes to a bounded repair loop; and only a human closes the Project. All of it was observed working against real Docker, real Hermes, a real model and a real Git repository — with the two honest gaps above recorded rather than papered over.
