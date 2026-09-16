# Phase 18.21 — Executable Acceptance Criteria & Model Routing Hardening

## 0. Executive Summary

Phase 18.20 ended with two named product blockers: an agent could do nothing and still be accepted because the project's own suite was already green, and the configured model could not finish a Repair inside the 5-minute execution budget.

The first blocker is **closed and proven live**. WorkItems now carry an explicit, machine-checkable acceptance contract that the Factory evaluates itself against the real workspace, after — never instead of — the mandatory build/test gate. In every live run of this phase the Factory caught an agent that had not produced the requested outcome, routed it into the existing repair loop, and only accepted the WorkItem once it could see the outcome on disk. The exact scenario that silently passed in Phase 18.20 now fails closed.

That change also delivered what Phase 18.20 could not: **a real Hermes repair that actually completed**. In three separate live runs a repair AgentRun ran in a real container (~90s), really created the missing file, and the Factory's own re-verification passed, taking the WorkItem to `Completed` through the ordinary pipeline. The repair was triggered by an acceptance failure rather than a test failure, which is precisely the §11 integration this phase asked for.

The second blocker is **partly closed**. Implementation, Repair, Review and Planner are now separately configurable models, the six placeholder ids that no provider can serve are gone from both shipped configurations, and a deterministic validator reports placeholder/empty/incoherent routing without ever calling a provider. What remains open is empirical, not architectural: this environment has exactly one reachable model, so the new per-role configuration could not be exercised with an actually faster Repair model.

**Final decision: NO-GO**, because §31 is explicit on two counts that were never reached live: Project completion was not exercised after real WorkItems completed, and human approval was not exercised on a live Project. The reason is model behavior, not Factory behavior — documented in §12 below with the measurements.

---

## 1. The no-op acceptance gap (what was actually wrong)

Before this phase, `VerificationGate` answered exactly one question: *is the project still healthy?* It ran `dotnet build` and `dotnet test` against the workspace and reported Passed/Failed.

That question is necessary and insufficient. Phase 18.20 measured the consequence live: the agent assigned "create STATUS.md containing READY" produced nothing at all, the fixture's suite was already green, so the gate passed and the WorkItem reached `Completed`. Nothing in the Factory ever asked whether the *requested outcome* existed. `WorkItem.Description` was only ever agent instructions; `WorkItem.AcceptanceCriteria` existed as a column but was read only as free text for the review prompt — nothing evaluated it.

---

## 2. Acceptance contract design

> **Build/test proves the project is healthy. Acceptance criteria prove the WorkItem happened. Both are the Factory's own judgement; neither consults the agent's report.**

Criteria travel as JSON in the **pre-existing** nullable `WorkItem.AcceptanceCriteria` column — §23's "smallest schema addition possible" turned out to be *none*:

```json
{"version":1,"criteria":[
  {"type":"ExpectedTextPresent","path":"src/Marker.cs","value":"MARKER_AFTER"},
  {"type":"ExpectedTextAbsent","path":"src/Marker.cs","value":"MARKER_BEFORE"}
]}
```

Design constraints honored:
- **No scripting, no commands** (§4). A criterion carries a type, a workspace-relative path and an expected string. There is no pattern language and no shell, so a contract can never become an injection vector regardless of who authored it — planner, operator, or an agent editing a plan.
- **Paths are untrusted input.** Every path goes through the existing `WorkspacePathGuard.EnsureWithinRoot`, so a criterion cannot read outside the Project workspace (traversal and symlink escapes both rejected).
- **No LLM judgement** (§7). Nothing in the evaluation path consults the agent's text, its structured result, or a model.

---

## 3. Supported criterion types

| Type | Meaning |
|---|---|
| `FileExists` | the file must exist |
| `FileAbsent` | the file must not exist |
| `ExpectedTextPresent` | the file must contain the value (ordinal substring) |
| `ExpectedTextAbsent` | the file must no longer contain the value |
| `ExpectedFileContent` | the file must equal the value, ignoring surrounding whitespace and line-ending style |
| `BuildPasses` / `TestsPass` | satisfied by construction — the build/test gate already ran and passed before acceptance is evaluated at all. Recognized so a plan may state them explicitly without being rejected as unsupported. |

---

## 4. Verification implementation

`IWorkItemAcceptanceVerifier` / `WorkItemAcceptanceVerifier` (Application layer, alongside the `WorkspacePathGuard` it reuses). Four deterministic outcomes:

| Acceptance outcome | `VerificationStatus` | Resulting WorkItem status |
|---|---|---|
| `Failed` — a criterion did not hold | `Failed` | `Repairing` (existing loop, existing counter) |
| `ContractInvalid` — malformed envelope, unsupported type, unsafe path, bad version | `InfrastructureError` | `Failed`, **without** consuming repair budget |
| `Passed` | `Passed` | continues to `WorkItemCompletionProcessor` |
| `NotSpecified` — empty, or legacy free text | `Passed` | historical build/test-only semantics (§9) |

`ContractInvalid` is deliberately *not* repairable: a broken acceptance contract is an operator/planner authoring error, and no repair agent can fix it, so burning three repair attempts on it would be pure waste.

`IVerificationGate` was **not** replaced (§5). `VerificationGate.VerifyAsync` still runs build then test, unconditionally, and only evaluates acceptance after both pass. `VerifyProjectAsync` (project-level) is untouched — acceptance is a WorkItem-scoped concept.

---

## 5. WorkItem integration & backward compatibility

- No migration. `dotnet ef migrations has-pending-model-changes` → *"No changes have been made to the model since the last migration."*
- A WorkItem with no criteria, or with the human prose historical WorkItems already carry in that column, reports `NotSpecified` and keeps its old semantics exactly. It is reported as its own distinct status precisely so no report can ever claim such a WorkItem was acceptance-verified (§9).
- Only text structurally shaped like the envelope is treated as a contract; free text is never guessed at.

---

## 6. Repair integration

Acceptance failures enter the **existing** repair architecture with no new state and no new counter (§11):

```
acceptance Failed -> VerificationStatus.Failed -> WorkItem.Repairing
  -> TaskDispatchScheduler prioritizes Repairing, increments RepairAttemptCount
  -> Repair AgentDefinition -> ordinary AgentRun -> real Hermes
  -> VerificationGate again (build + test + acceptance)
```

The repair agent receives the deterministic acceptance evidence through the existing `WorkItem.CurrentError` → `BuildRepairObjective` path, e.g.:

```
Acceptance criteria not met (2 of 2): expected file 'STATUS.md' to exist; file 'STATUS.md' does not exist
```

`ExecutionLimitsOptions.MaxRepairAttempts` remains authoritative.

---

## 7. Model routing design

The gap was narrower than it looked. Review and Planner already routed through `IModelRouter`; **Implementation and Repair never chose a model at all** — nothing in production set `AgentRun.Model`, so both silently used whatever the Hermes container had configured internally. That is why Phase 18.20 could not give Repair a faster model.

Changes, all inside the existing router rather than beside it:

1. `ModelRoutingOptions.AgentTypeCategories` — `AgentType -> task category` (`Implementation->coding`, `Repair->debugging`, `Test->testing`, `Review->code-review`, `Planner->planning`), so each role resolves through the existing `CategoryProfiles` catalog.
2. `AgentOrchestrator` resolves the role's model when the run carries none: `AgentRun.Model ?? router.SelectModel(categoryForAgentType)`. An explicit `AgentRun.Model` always wins; an unusable profile is not an error, the run simply carries no model and `HermesOptions.DefaultModel` (Phase 18.20) remains the last-resort fallback. Both new dependencies are optional constructor parameters, following the existing `HermesAgentExecutor` convention, so no existing construction site changed.
3. No model id is hard-coded in business logic — routing is entirely configuration.

---

## 8. Actual configured models

One model is reachable in this environment: `auto/coding:free`, routed through the local OmniRoute gateway at `http://host.docker.internal:20128/v1` (Hermes `--provider openrouter` with `OPENROUTER_BASE_URL` overridden; key from the `OMNIROUTE_API_KEY` environment secret, never reproduced here). It is the same repository-configured model every real-Hermes test has used since Phase 18.1.7 — not one chosen to make this phase pass (§26).

All four roles therefore ship pointing at it, which §14 explicitly permits ("Do not require them to be different"). The property that matters is that they are now *separately* configurable, proven by `EachRole_SelectsItsOwnConfiguredModel`, which puts four distinct models behind the four roles and asserts each resolves to its own.

The gateway refuses `/v1/models` (HTTP 401 for both the placeholder and the configured key), so no provider catalog could be enumerated and no alternative model id was invented.

---

## 9. Placeholder cleanup

Removed from **both** `AI.Factory.Api` and `AI.Factory.Worker` `appsettings.json`: `strong-reasoning-model-a`, `strong-reasoning-model-b`, `coding-optimized-model-a`, `coding-optimized-model-b`, `independent-strong-model-a`, `cheap-model-a`. These were what silently broke the live review stage in Phase 18.20.

`ModelRoutingOptions.Validate()` now detects, deterministically and offline (§15): placeholder ids, empty ids, models with no provider, categories with no candidates, categories referencing unknown models, and roles mapped to categories that have no profile. Both composition roots run it at startup and report each problem. It never calls a provider.

---

## 10. Deterministic test results

New coverage (§24):

| Area | Tests |
|---|---|
| Acceptance evaluation | 16 — pass, fail, multiple criteria, each criterion type, malformed JSON, unsupported type, unsupported version, empty list, missing required fields, legacy free text, whitespace/line-ending normalization, path traversal refused (2 shapes), determinism |
| No-op protection | 4 — no-op + green tests + failed acceptance -> `Repairing` and never `Completed`; work actually done -> `Completed`; legacy WorkItem keeps old semantics; invalid contract fails closed without consuming repair budget |
| Model routing | 9 — per-role selection for all four roles, default role mapping, roles sharing one model, placeholder detection, empty id, unknown model reference, empty profile, role mapped to missing category, coherent catalog reports nothing |

Full suite after all changes:

```
AI.Factory.Api.Tests          302 passed, 0 failed
AI.Factory.Application.Tests  831 passed, 0 failed
AI.Factory.Worker.Tests        88 passed, 0 failed
Total                        1221 passed, 0 failed, 0 skipped
dotnet build -c Debug        0 errors
dotnet build -c Release      0 errors
```

One test, `DatabaseResiliencePolicyTests.ExecuteAsync_CancelledDuringBackoffDelay_StopsImmediately_NoFurtherAttempt`, failed once in a full-suite run under concurrent Docker load and passed in isolation — the same known timing flake recorded in Phase 18.19, unrelated to any change here. No test was excluded or weakened; the previous suite was 1188.

---

## 11. Real Hermes results

Five live runs, all with real Docker, real Hermes containers, a real Git-initialized workspace, real `dotnet build`/`dotnet test`, and the real scheduler/repair/review path.

### Real Hermes implementation + acceptance — **PASSED**

The acceptance verifier ran live against the real workspace in every run and was correct every time. Representative (run C, Project `c3617279-f26d-4d63-a5d8-350ec2049f78`, T01 `e7e4d4f6-a3dd-4359-bac3-ca261ad125fb`):

```
[09:31:11] Running                                   <- real Hermes implementation agent
[09:33:06] Testing
           WorkItemAcceptanceFailed FailedCriteria=2 EvaluatedCriteria=2
           VerificationGateCompleted Status=Failed DurationMs=6755   <- build+test GREEN, acceptance caught the no-op
[09:33:14] Repairing
```

This is exactly the Phase 18.20 scenario, now failing closed. Observed in **5 of 5 runs**.

### Real Hermes repair — **PASSED**

Same run, continuing:

```
SchedulerRepairDispatched WorkItemId=e7e4d4f6... AgentRunId=5a737c64-75af-4f8b-a18c-bebb98dbfe94
                          AgentDefinitionId=5de3c8bb-4111-40b2-8040-d7cfd404f6b9 RepairAttemptCount=1
Hermes execution completed for agent Golden-Repair-Agent with exit code 0 in 90,349ms
VerificationGateCompleted Status=Passed DurationMs=4805
Hermes execution completed for agent reviewer with exit code 0 in 99,801ms
[09:41:32] Completed
T01 agent created STATUS.md: True
```

Every §25 requirement met: real Hermes finished, the source was really repaired (the file exists on the host, and `Completed` is only reachable because the Factory's own criteria found it), Factory verification passed afterwards, and the WorkItem reached `Completed` through the ordinary pipeline. Reproduced in **3 separate runs** (repair completing in ~90s each).

Also proven live: the repair loop converges and is correctly bounded. In one run acceptance failed three times, `RepairAttemptCount` advanced 1->2->3 on the real Repair AgentDefinition, and the third repair finally satisfied the criteria (`WorkItemAcceptancePassed`).

### Real Project completion — **NOT VERIFIED**

No run reached "all WorkItems Completed", so `ProjectCompletionGate` was never eligible, and `Testing -> Reviewing -> HumanReview` never ran live. The gate itself behaved correctly throughout — it is designed to stay inert until every WorkItem completes, and that condition was never met.

### Human approval — **NOT VERIFIED**

The Project never reached `HumanReview`, so neither the `ConflictException` bypass check nor `ApproveAsync` was exercised against a live Project. Both remain proven deterministically (`GoldenProjectLifecycleE2ETests`, green in this phase's suite).

### Manual Golden Path (§26) — **NOT ATTEMPTED**

Gated on the same chain that did not complete; it would have produced no evidence the automated runs did not already produce.

---

## 12. Timing measurements and the real remaining blocker

| Agent work | Duration | Outcome |
|---|---|---|
| Implementation, file-creation task | 38–115s | completes; sometimes does nothing at all (18–60s no-op) |
| **Repair, file-creation task** | **~90s** | **completes, acceptance then passes** |
| Repair, reconcile-a-failing-test task | >300s | cancelled at the 5-minute budget, **5 of 5 attempts** across Phases 18.20–18.21 |
| Review | ~100s | completes, does not block |

**A hypothesis I tested and disproved, stated plainly:** I suspected the slow repairs were caused by the 2000-character raw `dotnet test` dump embedded in the repair objective, since the fast repairs carried only a short acceptance summary. I tightened the bound to 600 characters — and the reconcile-a-failing-test repair still timed out at 302s. The bound change is retained because §12 independently asks for bounded diagnostics, but it did **not** fix the timeout and is not claimed to.

The actual difference is task shape, not prompt size. Creating a file is one tool call. Reconciling a failing test requires reading the test, reading the source, and deciding which side to change — many more tool calls, and this model exceeds the budget doing it.

**Classification (§27): Model.** Not a Factory defect. The Factory detected, routed and bounded every one of these correctly; the same container, workspace and code path completes the simpler repair in 90 seconds. Per §16 the production timeout was not raised, and per §28 no verification was weakened.

---

## 13. Live test shape

`LiveProjectLifecycleE2ETests` drives two dependent WorkItems, both carrying acceptance criteria, through the real Worker composition root against an isolated per-test SQLite file (WAL, pools cleared — the Phase 18.19 lock flake did not recur in any of the five runs). Two assertions that checked *agent behavior* rather than Factory behavior were converted to recorded observations, per §26's rule that model quality must not fail the Factory.

The reconcile-a-failing-test scenario was moved out of the lifecycle path after it blocked five runs, on §18's instruction that "the real E2E must prove the mechanism, not the stochastic quality of the model". Nothing about verification was relaxed to do this: both WorkItems still pass through the real build/test gate, real acceptance criteria and real review. That variant's measurements are recorded above rather than hidden.

---

## 14. Migration status

Zero. Verified: *"No changes have been made to the model since the last migration."* The acceptance contract rides in the existing nullable `AcceptanceCriteria` TEXT column (§23).

---

## 15. Review path (§21)

Unchanged from the Phase 18.20 fix: Review still receives an empty `WorkingDirectory` and therefore a fresh isolated scratch workspace, never the Project's. `ReviewAsync_NeverPassesAnIdentifierAsTheWorkingDirectory` keeps that regression locked, and the live runs confirm the reviewer executes and does not block.

---

## 16. Remaining model/provider limitations

1. **Repair of reasoning-shaped tasks exceeds the 5-minute budget** with `auto/coding:free`, reproducibly (5/5). Per-role model configuration now exists to address this; this environment has no second model to point it at.
2. **Instruction compliance is stochastic.** The same model sometimes performs the requested edit and sometimes returns in 18s having done nothing. This no longer misleads the Factory — such a run now goes to `Repairing` instead of `Completed` — but it means a run may need several repair attempts, and may exhaust them.
3. **Only one reachable model id**, and the gateway will not enumerate its catalog (401 on `/v1/models`).

## 17. Remaining production blockers

1. Live Project completion and live human approval are still unproven end to end. Both are blocked by (1) and (2) above, not by the Factory.
2. Acceptance criteria are only as good as what a plan declares. The Factory now enforces contracts rigorously, but nothing yet *requires* a WorkItem to carry one — a plan that declares none silently falls back to build/test-only semantics. Making acceptance criteria mandatory for planner-generated WorkItems is the natural next step; the Planner extension sketched in §8 of the phase brief was not needed for this phase and was not built.

---

## 18. GO / NO-GO

Against §30's GO list:

| Requirement | Status |
|---|---|
| Explicit WorkItem acceptance criteria exist | YES |
| Acceptance is Factory-owned | YES — deterministic, no LLM judgement |
| A no-op agent cannot reach Completed when acceptance fails | YES — proven live, 5/5 |
| Acceptance failure enters the existing repair loop | YES — existing counter, existing dispatch |
| No placeholder model required for normal execution | YES — removed from both configs, validator added |
| Implementation/Repair/Review/Planner separately configurable | YES |
| Repair model finishes inside the execution policy | PARTIAL — yes for file-shaped repairs (~90s); no for reasoning-shaped repairs (5/5 over budget) |
| Real Hermes executes the Repair Agent | YES |
| Repair modifies the actual Project workspace | YES |
| Factory verification passes afterward | YES |
| WorkItem reaches Completed normally | YES — 3 runs |
| Real WorkItems complete -> Project reaches HumanReview -> approval completes it | NO — never reached |

```text
DETERMINISTIC TESTS:              PASSED   (1221 passed, 0 failed)
REAL HERMES IMPLEMENTATION:       PASSED   (acceptance evaluated live, no-op caught 5/5)
REAL HERMES REPAIR:               PASSED   (real repair finished, source fixed, verification passed, 3 runs)
REAL PROJECT COMPLETION:          NOT VERIFIED
MANUAL GOLDEN PATH:               NOT VERIFIED

Executable Acceptance & Model Routing: NOT VERIFIED
Final Decision: NO-GO
```

§31 applies on two counts — "Project completion was not exercised after real WorkItems completed" and "Human approval was not exercised on the live Project" — so NO-GO is the honest result, even though the phase's headline objective was met and the blocker that defeated Phase 18.20 is gone.

What changed materially: the Factory can no longer be fooled by an agent that does nothing, it proved a real Hermes repair end to end for the first time, and the model layer is now configurable per role with no placeholders and a validator that catches the class of misconfiguration that silently broke the live review stage one phase ago. What is left is a model that is fast and reliable enough to carry a two-WorkItem project to the finish — a provisioning decision, not an engineering one.
