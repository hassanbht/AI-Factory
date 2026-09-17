# Phase 18.23 — Reliable Planner Output & Real Plan-to-Execution E2E

> Every live result below was observed in a real run and copied from its log.
> Nothing here is inferred. The one thing this phase could not finish — a single
> run containing *both* a real repair and project verification — is reported as
> unverified rather than argued around (§11, §25, §27).

## 0. What this phase had to answer

Phase 18.22 closed every stage downstream of the plan and left exactly one gap:

```
REAL PLANNER E2E:  NOT VERIFIED   (model produced no valid structured plan)
```

§24 forbids assuming why. The phase had to establish, from live evidence,
whether that failure came from the model, from Hermes/the provider, or from the
Factory's own structured-result boundary — and then fix whichever it was.

---

## 1. The diagnostic answer (§24)

**Both the model and the Factory. The Factory's boundary was the decisive half,
and it is now fixed.**

A live Hermes container was run directly with the Phase 18.22 Planner prompt,
against the same provider and model every real-Hermes test in this repository
has used since Phase 18.1.7. Three observations, all from captured stdout:

### 1.1 The model does emit the envelope

It is not true that the model "produced nothing the parser could read". It
produced the full `{"version":1,"status":"Succeeded",…,"data":{"plan":…}}`
envelope on every probe. What it failed at was *finishing* long JSON.

### 1.2 The real failure shape — and why the Factory could not see past it

One probe produced this (elided; the raw text is in the session record, not
reproduced in full here):

```
{"version":1,"status":"Succeeded",…,"data":{"plan":{"version":1,…,"dependsOn":[],

Wait — the plan needs to cover both documentation files. Let me produce the complete and correct version:

{"version":1,…,"dependsOn":[]

Hmm, I need both tasks in one JSON. Let me output the final correct version:

{"version":1,"status":"Succeeded",…}      <- complete, valid, correct
```

The agent abandoned two attempts in prose and then emitted a **complete, valid
envelope**. `StructuredAgentResultParser` took the **first** `{` and scanned to
its matching `}`. The first object never closes, so the scan ran through the
prose and into the later attempts, consuming the good envelope inside a garbage
span — and the run was reported as having produced no structured result at all.

That is a Factory-side defect, and §24 says: *if the Factory boundary is the
cause, fix the Factory.*

### 1.3 A second model failure, independent of the above

A different probe closed the `tasks` array early and then emitted
`…]}}],"docs.contributing","title":…` — genuinely malformed JSON, not a
selection problem. Every such sample was a plan the model had run out of budget
to finish: it had grown the plan to four tasks with multi-sentence descriptions.

---

## 2. Workstream A — the parser boundary (§2)

`StructuredAgentResultParser` now collects **every** balanced `{...}` region in
one linear pass (string-literal aware, so a brace inside a quoted value still
never affects the depth count), evaluates them **newest-first**, and returns the
agent's **last complete, valid envelope**.

What this is:

- a **selection** rule over objects the agent actually finished writing;
- deterministic, and documented on the type itself;
- bounded (at most 64 candidates are deserialized, so a brace-heavy blob cannot
  turn the scan into a cost).

What this is explicitly **not** (§5):

- nothing is inferred, defaulted or invented;
- nothing is merged across candidates — a candidate is accepted whole or skipped
  whole;
- an unfinished envelope with no complete alternative still fails closed, with
  no salvaged prefix;
- the envelope's required fields (`version`, `status`, supported version,
  recognized non-`Invalid` status) are still enforced exactly as before, per
  candidate.

Failure diagnostics changed in one way only: when nothing validates, the
reported reason comes from the **most recently closed** candidate — the largest
object the agent actually finished — rather than from the first brace in the
text.

## 3. Workstream A — the prompt (§4)

`ProjectPlanner.BuildObjective` was rewritten against §4's checklist. The
substantive changes over Phase 18.22's version:

| §4 requirement | Change |
|---|---|
| 1. machine-consumed, not conversational | Opens with it explicitly, and with "a single malformed character rejects the entire plan" |
| 2/3. exact envelope and required fields | The full envelope, the exact task shape and the exact shape of **each** criterion type are now shown literally, instead of one placeholder line |
| 4. allowed types/priorities | Still projected from `WorkItemType` and `ProjectPlanService.ValidPriorities` |
| 6/7/8. criterion types, which need `path`, which need `value` | Projected from `AcceptanceCriterionType` and grouped exactly as `AcceptanceCriteriaContract` groups them — the prompt cannot describe a rule the validator does not hold |
| 9. forbid shell/scripts/regex/NL | Kept, extended to globs and to `type`/`path` as well as `value` |
| 13. output ONLY the object | Kept, plus an explicit ban on markdown and code fences |
| (§12) | New: emit the SMALLEST plan, at most `MaxRecommendedTasks` (3), one task per concrete repository change, and never a task about the plan itself |

Two anti-gaming rules were added because live output showed both: a ban on
`BuildPasses`/`TestsPass` as a task's **only** criterion (they pass before the
task starts, so they prove nothing about it), and a one-short-sentence limit on
descriptions, since long prose is what exhausted the budget the JSON needed.

`MaxRecommendedTasks` is prompt guidance only. The Factory does **not** reject a
plan for being large — validation authority is unchanged (§4's closing rule).

## 4. Diagnostics (§6)

`PlannerDiagnosticStage` names the seven stages §6 lists.
`PlannerCandidatePlanException` now carries the stage that rejected it
(acceptance-contract failures are tagged as such rather than being
indistinguishable from schema failures), and `ProjectPlanner.MaterializeAsync`
logs one line per failure:

```
PlannerStageFailed Stage={Stage} AgentRunId={AgentRunId} Reason={Reason}
```

replacing the older, less specific `PlannerCandidateInvalid` /
`PlannerRuntimeFailed` / `PlannerPlanValidationFailed` messages. The parse stage
now surfaces the parser's own recorded reason — which the previous code
persisted onto the structured result and then discarded in favour of the
constant string `NoValidStructuredResult`.

Nothing new is logged that was not logged before: no API keys, no headers, no
prompts, and **no raw model output**. The raw output stays where it already was,
on `AgentRun.Output`, for deliberate inspection.

## 5. Structured-output capability (§8)

Investigated directly, not assumed.

**The provider supports it.** A direct call to the configured OpenAI-compatible
gateway with `"response_format": {"type": "json_object"}` returned HTTP 200 and
a clean JSON body.

**Hermes does not expose it.** `hermes --help` (full output inspected) has no
flag for response format, JSON mode, schema or structured output. The generated
`config.yaml` (1,985 lines, inspected) has no such key for the agent's own
inference either: the only `extra_body` escape hatch it documents is scoped to
auxiliary side-tasks (`session_search`, `title_generation`, `web_extract`,
context compression) and does not reach the agent's chat loop. `-z/--oneshot`
is documented as printing only the final response text.

Reaching `response_format` from the Factory would therefore mean inventing an
interface Hermes does not have, which §8 forbids. The textual JSON contract is
kept, and the prompt/parser boundary was hardened instead.

```
§8 CLASSIFICATION: NATIVE_STRUCTURED_OUTPUT_UNAVAILABLE
```

with the honest nuance that this is a **Hermes integration-surface** limit, not
a provider limit — the provider underneath would accept it today.

## 6. Model routing (§7)

Unchanged, and re-confirmed live. Resolution order is still
`AgentRun.Model` -> role-specific routing -> configured default. Observed in the
live run:

```
AgentRunModelResolved AgentType=Planner        Category=planning ModelId=<configured>
AgentRunModelResolved AgentType=Implementation Category=coding   ModelId=<configured>
```

No vendor is hard-coded, the live harness stays environment-driven
(`AI_FACTORY_LIVE_BASE_URL`, `AI_FACTORY_LIVE_MODEL`,
`AI_FACTORY_LIVE_<ROLE>_MODEL`), and no key material appears in source, tests,
configuration or this report.

## 7. Security and trust boundaries (§16)

Nothing in the diff touches `WorkspacePathGuard`, `IWorkspaceResolver`, the
workspace isolation, process execution, the secret provider, or the acceptance
contract's parsing. The parser change is pure in-memory text processing with no
filesystem or network access. Planner output remains untrusted input that is
accepted or rejected whole, and structural validity still grants it no
authority.

## 8. A second boundary defect the live §11 run exposed

Mid-run, an Implementation agent answered with a valid envelope whose
`metadata` carried a non-string value:

```
StructuredAgentResultInvalid Reason=Malformed
Message=... could not be converted to System.String. Path: $.metadata.files_changed
```

`metadata` is a free-form side channel — nothing in the Factory reads a
decision out of it — yet binding it as a string map discarded the **entire**
envelope, including the `data.execution` report the Factory does use. The
Phase 18.20 resilience path caught it (`WorkItemExecutionMetadataUnavailable`
-> `Testing`), and acceptance still had to pass on its own, so nothing was
wrongly accepted. But the same mismatch on a Planner run would throw away a
perfectly good plan.

Non-string metadata values are now kept as their raw JSON text. No required
field's validation is relaxed: `version`, `status`, the supported version and
the non-`Invalid` status check are enforced exactly as before.

---

# Live evidence

## 9. Run 1 — real Planner to human approval (§11/§22)

`LivePlannerE2ETests.RealPlanner_GeneratedWorkItems_ExecuteToProjectHumanReview`
— **PASSED in 15m 30s**.

### Planner

```
ProjectId              55284126-103a-4246-9a66-d6b72721207f
Planner AgentRunId     9e6a8ce7-e37e-4c1d-ac0b-5a4d1ebd85c7
Hermes                 exit code 0 in 37,176ms
Model routing          AgentType=Planner Category=planning ModelId=<configured>
Structured parse       Succeeded
Plan validation        Passed
PlanHash               AC31CD7AA95DE63E2F2031C9AED7E2FB445EF0A426B30BE860A562E355B4E2EF
Generated WorkItems    272763e5-32d3-41fc-8f9e-88bc4ec837e3  (docs.status)
                       7112dda4-3389-4d8d-88bb-ed5812355b8e  (docs.release-notes)
```

### Plan

```
docs.status           Implementation   dependsOn []                acceptance: Valid (2 criteria)
docs.release-notes    Implementation   dependsOn [docs.status]     acceptance: Valid (2 criteria)
```

The persisted dependency graph was asserted to equal the plan's declared graph
exactly — nothing dropped, nothing invented.

### Execution

```
13:51:11  docs.status         Ready
14:00:04  docs.status         Running
14:01:20  docs.status         Testing      WorkItemAcceptancePassed EvaluatedCriteria=2
                                           VerificationGateCompleted Status=Passed (7.6s)
14:05:48  docs.status         Completed    RepairAttemptCount=0

          docs.release-notes  dispatched only after docs.status passed
          docs.release-notes  Running -> Testing
                                           WorkItemAcceptancePassed EvaluatedCriteria=2
                                           VerificationGateCompleted Status=Passed (5.0s)
14:05:48  docs.release-notes  Completed    RepairAttemptCount=0
```

AgentRuns for the Project — one Planner run and one Implementation run per
generated WorkItem, all `Succeeded`, no hand-made WorkItem anywhere:

```
9e6a8ce7  Planner         Succeeded   workItem=(none)
fd87018c  Implementation  Succeeded   workItem=272763e5
a33fc57b  Implementation  Succeeded   workItem=7112dda4
```

### Project

```
14:05:48  Project -> Testing
          ProjectVerificationCompleted Status=Passed DurationMs=5407
14:05:55  Project -> Reviewing
14:05:57  Project -> HumanReview
          ConflictException asserted on a direct HumanReview -> Completed transition
          ApproveAsync -> Project.Completed
```

No Project or WorkItem status was set by hand. The only manual actions were
the operator-level Project transitions the HTTP API also exposes
(`Analyzing -> Specification -> Architecture -> Planning` before planning,
`Planning -> Implementation` after it) and the explicit human approval.

### What run 1 did not show

**No repair was exercised.** Both Planner-generated WorkItems satisfied their
acceptance contracts on the first attempt (`RepairAttemptCount=0`). The repair
path is therefore *not* proven in this phase by run 1, which §26.6 requires.
This is recorded as an observed gap, not explained away.

---

## 10. Run 2 — real repair in the Planner path, and a genuine failure

The whole live class was re-run after the §8 metadata fix. **The E2E FAILED.**
It is reported in full because it proves one §26 condition run 1 could not, and
because concealing it would be exactly what §27 forbids.

### Plan (3 tasks this time — the model's own choice)

```
Planner AgentRunId  71d00c13-75e2-447f-bda9-017b337d3f09  (Succeeded, 73s)
PlanHash            9BBD6C6F7377210E505FCA4E50134C6B891A0B57DB3A131DA72E3713FB6544D0

docs.status              Implementation  dependsOn []                     criteria Valid (2)
docs.release-notes       Implementation  dependsOn [docs.status]          criteria Valid (2)
verify.build-and-tests   Test            dependsOn [docs.release-notes]   criteria Valid (2)
```

### Execution — real acceptance failure, real repair, real dependent dispatch

```
14:14:50  docs.release-notes  Running
14:16:24  docs.release-notes  Testing     WorkItemAcceptanceFailed FailedCriteria=2/2
                                          VerificationGateCompleted Status=Failed (7.0s)
14:16:30  docs.release-notes  Repairing   SchedulerRepairDispatched AgentRun=93a55762 RepairAttemptCount=1
14:16:32  docs.release-notes  Running
14:18:49  docs.release-notes  Testing     WorkItemAcceptancePassed EvaluatedCriteria=2
14:22:32  docs.release-notes  Completed   RepairAttemptCount=1
14:22:32  docs.status         Completed   RepairAttemptCount=1
                                          (same cycle: acceptance failed 2/2 -> repair
                                           AgentRun=c8882ddd -> WorkItemAcceptancePassed)
14:22:34  verify.build-and-tests  Running
14:27:33  verify.build-and-tests  Cancelled   "Agent execution was cancelled"
```

**Two real repairs completed on Planner-generated WorkItems.** In both cases the
build and tests had already passed and the Factory still rejected the work,
because the acceptance contract *the Planner itself wrote* was not satisfied —
then a real Repair AgentRun fixed the workspace and acceptance passed on
re-verification. That is §26.6 and §21.E's repair leg, observed.

### Why it failed

The third task, `verify.build-and-tests`, ran for ~5 minutes and hit the shipped
`ExecutionTimeout`, ending `Cancelled` — a terminal status, so the WorkItem
never reached `Completed` and the Project never reached verification.

**The production timeout was not raised** (§14). The cause is the model's plan:
it added a task whose entire job is to build the project and run its tests —
work the Factory performs itself after *every* WorkItem, and which the
`BuildPasses`/`TestsPass` criteria already express. The Phase 18.22 prompt
forbade tasks *about the plan*; it did not forbid a task that re-runs
verification. That gap is now closed in `BuildObjective`, as guidance only — the
Factory still accepts such a plan if a model writes one, because it is
structurally valid and rejecting it would be a new validation rule this phase
was not asked to add.

### Reliability (§13/§23), run 2's N=3 loop — 3 successes in 5 attempts

```
| Run | Hermes    | Structured Result | Plan Valid | Criteria Valid | Persisted | Duration | Plan |
|---|---|---|---|---|---|---|---|
| 1 | Succeeded | Succeeded | True  | True  | True  |  83s | docs.release-notes<-[docs.status] docs.status |
| 2 | Succeeded | Invalid   | False | False | False | 247s | - |
| 3 | Succeeded | Invalid   | False | False | False | 229s | - |
| 4 | Succeeded | Succeeded | True  | True  | True  |  67s | docs.release_notes<-[docs.status] docs.status |
| 5 | Succeeded | Succeeded | True  | True  | True  |  42s | docs.release-notes<-[docs.status] docs.status |

Run 2 failure: PlannerStructuredParse — "'}' is invalid without a matching open. Path: $.data"
Run 3 failure: PlannerStructuredParse — "Structured result is missing required field 'version'."
```

Both failures are the model emitting broken JSON, both were refused, both
persisted nothing, and — unlike before this phase — both say exactly which
stage failed and why.

**Attribution (§23).** Neither failure is Hermes, the provider, the parser, the
Factory's validation, dependency registration or persistence: in both the
container exited 0 and the Factory read the agent's own output correctly and
found it malformed. Category: **Model.**

---

## 11. Run 3 — blocked, then re-run on a different provider

### 11a. The blocker

After run 2, one prompt rule was added: never plan a task whose job is to build
the project or run its tests, because the Factory does that itself after every
WorkItem. Run 3 was to be the run judged against §21.E, with both a real repair
and project verification in one pass.

It never executed. The machine was shut down mid-run (its log stops after 61
lines, before any test result), and on restart the provider was gone:

```
[429] Felo thread creation failed                                  (felo-web, rate-limited)
[403] OpenCode's free tier can only be used from within OpenCode   (opencode)
[400] Upstream request failed: Model is unavailable                (deepseek)
All models failed ... terminal={"status":502} decisions=11..13
```

The `auto/coding:free` combo fans out across 11–13 free upstreams and every one
now fails. The last successful upstream call in the gateway's log is
`2026-09-16T14:48` — the minute run 2 finished. Docker was restarted and is
healthy; the Factory is unchanged and its deterministic suite is green. This is
an upstream availability change, not a regression and not something the Factory
can route around on its own.

Per §20, live success is not claimed for a run that did not happen.

### 11b. Provider change — and why runs 1–2 and run 3 are not comparable

AgentRouter was then enabled in the gateway, but its budget is also exhausted
(`[402] Budget pool quota has been exhausted`, `All 1 connection(s) credits
exhausted`). Run 3 was therefore executed against **a different provider and a
different model**: OpenRouter directly, on a free model with sufficient context
for Hermes.

```
Runs 1-2:  OmniRoute gateway            model auto/coding:free
Run 3:     OpenRouter (api/v1) direct   model nex-agi/nex-n2.5-pro:free
```

This matters for reading the evidence, so it is stated plainly rather than
buried: **run 3's numbers are not comparable with runs 1–2's, and the §12
reliability table below deliberately excludes it.** What run 3 can still settle
is the §21.E question — whether the Factory can carry one plan through repair
*and* project verification in a single pass — because that is a property of the
Factory's own chain, not of which model wrote the plan.

No code, configuration or test was changed for the switch. The harness is
env-driven by construction (§7), so the provider swap is three environment
variables and nothing else:
`AI_FACTORY_LIVE_BASE_URL`, `AI_FACTORY_LIVE_MODEL`, and the existing
`ISecretProvider` secret name. The key itself was supplied by the operator at
run time and appears nowhere in source, configuration, tests or this report.

### 11c. Run 3a — rejected on one character

```
Planner AgentRunId  52d53645-7f36-4c6a-b70c-32cb255ad5d2
Model routing       AgentType=Planner Category=planning ModelId=<configured free model>
Hermes              exit code 0 in 82,105ms
PlannerStageFailed  Stage=PlannerStructuredParse
                    Reason=Structured result is missing required field 'version'.
```

The raw output was recovered from the run's own database before cleanup. The
model got the *content* entirely right — two tasks, the correct order, the
correct dependency, and exact-content acceptance contracts:

```
docs.status         Documentation  dependsOn []             ExpectedFileContent STATUS.md        = READY
docs.release-notes  Documentation  dependsOn [docs.status]  ExpectedFileContent RELEASE_NOTES.md = INITIAL RELEASE
```

and then emitted one stray character between the two tasks:

```
..."value":"READY"}]}},"{"planKey":"docs.release-notes",...
                       ^ a quote where "," should have been
```

That `"` opens a string, so every brace after it counts as string content and
the object structure collapses — which is why the newest complete candidate the
parser could close lacked `version`.

**This is the model, not the Factory**, and it is precisely the case §5 says must
fail closed. Nothing was persisted. Repairing that character would be the silent
plan repair §5 forbids, so no Factory change was made for it. It is recorded here
because it is the sharpest available illustration of the phase's central finding:
the model understands the contract and fails at *emitting* it.

---

### 11d. Runs 3b–3d, and a direct measurement of the constraint

Run 3b's Planner **succeeded** — a valid, persisted plan with the correct
dependency and valid criteria:

> The gate timeout described below was first blamed on operator error (a
> concurrent solution build). That was wrong, and chasing it properly uncovered
> a third real Factory defect — see §11e.

```
| 1 | Succeeded | Succeeded | True | True | True | 61s | docs.release-notes<-[docs.status] docs.status |
  docs.release-notes  Documentation  dependsOn [docs.status]  criteria Valid (1)
  docs.status         Documentation  dependsOn []             criteria Valid (1)
```

It then failed downstream: `VerificationGateCompleted Status=TimedOut
DurationMs=300018`. That is honestly attributable to operator error — a full
solution `dotnet build` was started on the same machine while that gate was
running, and MSBuild/NuGet contention is the most likely cause. Worth recording
regardless: after a **timed-out** gate no repair was dispatched, because a
timeout is not a verification failure, so the WorkItem sat in `Testing` until the
test's own wait expired. Changing that would be a repair-policy redesign, which
§18 forbids in this phase.

Run 3c failed at the Planner (same malformed-JSON class as 3a).

Rather than spend ~30 minutes per attempt to sample a stochastic first gate, the
constraint was measured directly: the exact production prompt was sent to each
available free model, and the reply was scored with the same newest-first
balanced-object rule the Factory uses.

```
nex-agi/nex-n2.5-pro:free                  0/3   bad(919ch) bad(907ch) bad(947ch)
nvidia/nemotron-3-super-120b-a12b:free     1/3   ok(2t) bad(0ch) bad(0ch)
nvidia/nemotron-3.5-lightning:free         1/3   ok(2t) bad(11701ch) bad(123ch)
poolside/laguna-s-2.1:free                 1/3   bad(988ch) ok(2t) http429
```

Against the previous provider's model the same Factory code measured **8/10**.
The difference is the model, not the Factory: the code, the prompt and the parser
were identical across both. This is the §24 answer stated quantitatively — with
the boundary defects fixed, planner reliability is now a pure function of which
model is behind the harness.

### 11e. A third Factory defect: verification builds could hang until timeout

Run 3d timed out at the gate again — with **nothing else running on the
machine**, which disproved the concurrent-build explanation and forced a real
diagnosis.

Measurements, all in the run's own leftover workspace:

```
gate (live)                      Status=TimedOut  DurationMs=300019   (= BuildTimeoutMinutes 5)
dotnet build, same workspace                      2s
dotnet test,  same workspace                      3s
dotnet build, fresh git clone, gate's exact form  3s
git status in the workspace:  only obj/ and bin/  -> the build HAD run and succeeded
```

The cause is the interaction between two reasonable designs:

- `SystemProcessRunner` redirects stdout/stderr, starts async reading, and waits
  with `WaitForExitAsync` — which does not return until the child's output
  streams reach EOF.
- `dotnet build` leaves **persistent MSBuild worker nodes and a VBCSCompiler
  server** running by default, so later builds start faster. Those daemons
  inherit the redirected stdout handle.

The build finishes; the daemons keep the pipe open; EOF never arrives; the gate
blocks until its timeout and reports a **successful build as `TimedOut`**.

Confirmed directly — after run 3d the daemons it had spawned were still alive,
with start times matching the gate's build to the second:

```
dotnet        10496  started 07:37:44   (run 3b's gate build)
dotnet        12352  started 09:27:44   (run 3d's gate build)
VBCSCompiler  25948  started 09:27:46
```

It only reproduces when the gate is the **first** thing to build after a reboot.
Once the daemons exist, later builds reuse them and inherit nothing — which is
why Phases 18.19–18.22 never saw it, and why it appeared the moment this session
resumed on a freshly restarted machine. On a cold CI agent it would be the normal
case, not the exception.

**Fix (§24 — the boundary is the Factory's, so the Factory is fixed):**
`DotNetBuildExecutor` and `DotNetTestExecutor` now pass
`-nodeReuse:false -p:UseSharedCompilation=false`. No timeout was raised (§14),
no verification was weakened, and the gate's semantics are unchanged — a build
that fails still fails. Two deterministic tests capture the arguments so the
flags cannot be silently dropped.

**Verified under the exact failing condition.** The daemons were killed to
restore the cold state that produced the 300s hang, then the real-build gate
tests were run:

```
MSBuild/VBCSCompiler daemons before:  0 (cold - the state that hung)
VerificationGateRealBuildIntegrationTests + DotNetBuildExecutorIntegrationTests:
                                      6 passed, 0 failed, 3s
MSBuild/VBCSCompiler daemons after:   0  -> the pipe closes, so the gate cannot hang
```

This is the third defect in this phase that only live execution could surface,
after the first-balanced-object parse rule and the string-typed `metadata` bind.
It is also the most consequential of the three: it turned **passing** builds into
reported timeouts, and on a cold CI agent it would be the normal case rather than
a rare one.

### 11f. Run 3g — the fix proven live, and the remaining wall

Run 3g is the first attempt to reach the gate with the daemon fix in place, and
it settles that question:

The plan it produced was correct in every respect the Factory checks — right
shape, right dependency direction, real file-oriented acceptance criteria:

```
| 1 | Succeeded | Succeeded | True | True | True | 51s | docs.release_notes<-[docs.status] docs.status |

  docs.status         dependsOn []                  Create STATUS.md         criteria=Valid (2)
  docs.release_notes  dependsOn [docs.status]       Create RELEASE_NOTES.md  criteria=Valid (2)
```

```
Planner              PlanMaterialized TaskCount=2            (valid, persisted)
WorkItem dispatched  real Hermes implementation
VerificationGate     Status=Failed DurationMs=10386          <- 10.4s, not 300s
                     WorkItemAcceptanceFailed 2/2
Repair 1             SchedulerRepairDispatched RepairAttemptCount=1
                     -> acceptance failed 2/2, gate 5.2s
Repair 2             RepairAttemptCount=2 -> acceptance failed 2/2, gate 5.3s
Repair 3             RepairAttemptCount=3 -> acceptance failed 2/2, gate 7.4s
```

The gate that previously hung for 300s now completes in 5–10 seconds, every
time, under real load. **§11e's fix is verified in the live pipeline**, not just
in isolation.

The run still did not finish, and the reason is the model. After four real agent
executions — one implementation and three repairs — the workspace contained:

```
Demo.slnx   src/   tests/        (and no STATUS.md)
```

The agent never created the file it was asked for. The Factory's behaviour was
exactly right: it refused the WorkItem four times, each time because its own
acceptance check found the requested evidence absent, and then stopped at the
bounded repair limit instead of accepting it or retrying forever. This is the
no-op-agent gap Phase 18.21 was built to close, working as designed against a
weaker model. Final verdict: `Fehler … [30 m 55 s]`.

One small test-side observation, recorded for accuracy rather than as a defect:
`LivePlannerE2ETests` waits for the plan's tasks in the order
`IProjectPlanService` returns them, which is `PlanKey` order — here
`docs.release_notes` before `docs.status`. The scheduler correctly dispatched
`docs.status` first (it is the prerequisite), so the timeout message named a
different WorkItem than the one that actually exhausted its repairs. The outcome
is unaffected — every task must complete either way — but a future revision
should wait on the dependency order to keep failure messages pointing at the
right item.

---

## 12. Reliability across everything that did run (§13/§23)

Ten real Planner executions, two sessions, same fixture:

| Session | Attempts | Persisted a valid plan | Notes |
|---|---|---|---|
| 1 — N=3 test | 3 | 3 | 3/3 |
| 1 — E2E | 1 | 1 | |
| 2 — E2E | 1 | 1 | 3-task plan |
| 2 — N=3 test | 5 | 3 | 2 malformed-JSON failures |
| **Total** | **10** | **8** | |

Both sessions reached the required 3 successes. Every failure was the model
emitting broken JSON; every failure was refused; **no failure ever persisted a
WorkItem.** Attribution for all failures: **Model**. None were Hermes, provider,
parser, Factory validation, dependency registration or persistence.

---

## 13. §26 conditions, individually

| # | Condition | Status | Evidence |
|---|---|---|---|
| 1 | Real Planner executes | PASSED | 10 real Hermes Planner runs |
| 2 | Real Planner produces a valid structured plan | PASSED | 8/10 attempts |
| 3 | Generated tasks contain valid acceptance criteria | PASSED | every persisted task parsed `Valid` |
| 4 | Plan persisted through the normal production path | PASSED | `ProjectPlanPersisted` / `PlannerPlanMaterialized` |
| 5 | Real generated WorkItems execute | PASSED | runs 1 and 2 |
| 6 | At least one real repair completes | PASSED | run 2, two repairs |
| 7 | Dependency scheduling works | PASSED | dependent item dispatched only after its prerequisite completed, both runs |
| 8 | Project verification succeeds | PASSED | run 1, `ProjectVerificationCompleted Status=Passed` |
| 9 | Deterministic suite remains green | PASSED | 1250 passed, 0 failed |
| 10 | No security/trust boundary weakened | PASSED | §7 above |

Each of the ten is satisfied. **§21.E is not**: it asks for one complete live
path containing all of these *together*, and no single run had both a real
repair and project verification. Run 1 had the full chain without a repair; run
2 had two real repairs and dependent dispatch but died on a third task before
project verification. That combination is the only thing missing, and run 3
existed to close it.

---

## 14. Honest limits

**Model.** The binding constraint, unchanged in kind since Phase 18.19 and now
measured: 8/10 valid plans. Failures are unfinished or malformed JSON. The
parser fix removed one whole class of these (a self-correcting agent whose final
envelope was previously unreachable); the remainder are the model genuinely
emitting broken JSON, which the Factory refuses.

**Hermes / provider.** Two distinct limits. Hermes exposes no structured-output
mechanism for the agent's own inference even though the provider beneath it
supports `response_format` (§5) — closing that would require inventing an API
Hermes does not have. Separately, the free upstream tier this repository's live
harness has used since Phase 18.1.7 became unavailable mid-phase, which is what
stopped run 3.

**Factory.** Three real defects found and fixed here, none of which any
deterministic test could have caught, because all three only appear when real
processes and real model output meet:

1. the first-balanced-object parse rule (§1.2),
2. the string-typed `metadata` bind (§8),
3. the MSBuild-daemon pipe hang that reported successful builds as `TimedOut`
   (§11e) — the most consequential, and the one that would hit every cold CI
   agent.

None weakened a check; none was worked around with a longer timeout. Two rough
edges are recorded rather than fixed, because §18 forbids redesigning repair
policy in this phase: an AgentRun that exceeds `ExecutionTimeout` ends its
WorkItem as `Cancelled`, a terminal state, so a timed-out task never gets a
repair attempt the way a failed one does; and a gate that reports `TimedOut`
likewise does not lead to a repair, so the WorkItem simply waits.

---

## 15. Final report (§27)

```text
REAL PLANNER E2E:              PASSED   (run 1: Planner -> WorkItems -> verification
                                         -> HumanReview -> human approval)
PLANNER REPRODUCIBILITY:       3/3 in each of two sessions; 8/10 attempts overall
STRUCTURED RESULT:             PASSED
ACCEPTANCE CONTRACT:           PASSED
REAL PLAN PERSISTENCE:         PASSED
REAL GENERATED EXECUTION:      PASSED
REAL REPAIR:                   PASSED   (run 2: two repairs on Planner-generated WorkItems)
DEPENDENCY DISPATCH:           PASSED
PROJECT VERIFICATION:          PASSED   (run 1)
DETERMINISTIC TESTS:           1250 passed, 0 failed

MODEL LIMITATION:              Cannot reliably finish long JSON. 8/10 valid plans on
                               the original provider; 0/3-1/3 on every free model
                               measured after it became unavailable. On the weaker
                               models the agent also never performed the requested
                               file edit across four executions. Every failure was
                               refused and persisted nothing.
HERMES/PROVIDER LIMITATION:    No structured-output mechanism is exposed for the
                               agent's own inference, though the provider supports
                               response_format. Separately, the free upstream tier
                               used since Phase 18.1.7 became unavailable mid-phase,
                               forcing a provider switch mid-evidence.
FACTORY LIMITATION:            Three live-only defects found and FIXED: first-
                               balanced-object parsing; string-typed metadata bind;
                               and MSBuild daemons holding the redirected pipe, which
                               reported successful builds as TimedOut. No timeout was
                               raised and no check weakened. Still open, untouched per
                               §18: neither a Cancelled AgentRun nor a TimedOut gate
                               leads to a repair attempt.

SINGLE-RUN §21.E PATH:         NOT VERIFIED
                               (repair and project verification were each observed,
                                but never together in one run)

FINAL DECISION:                NO-GO
```

**Why NO-GO despite ten passing conditions.** §25 is explicit that GO must not
be declared on partial evidence, and §21.E asks for one *complete* path, not a
union of separate runs. No single run shows Planner → generated WorkItems →
implementation → acceptance → **repair** → dependent WorkItem → **project
verification** together:

- run 1 had the whole chain through human approval, but no repair was needed;
- run 2 had two completed repairs and dependent dispatch, but died before project
  verification;
- run 3g had the Planner, acceptance rejection and three real repair dispatches,
  but the model never did the work, so nothing completed.

Calling that GO would assert something no log shows. What is missing is a
*combination*, not a capability — every individual leg has been observed working
on Planner-generated WorkItems, and the Factory refused incorrectly-done work
every single time it was offered.

**What would settle it:** the same test, unchanged, against a model that can both
emit valid JSON and perform a one-line file edit. The Factory side is done; three
of its own defects were found and fixed along the way, and the deterministic suite
is green at 1250. Nothing in this report should be read as the Factory needing
further change to pass — the evidence says the opposite.
