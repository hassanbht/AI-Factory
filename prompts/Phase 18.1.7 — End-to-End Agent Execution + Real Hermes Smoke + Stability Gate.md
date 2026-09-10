# Phase 18.1.7 — End-to-End Agent Execution + Real Hermes Smoke + Stability Gate

## Phase Objective

This phase is the **first real end-to-end validation gate** for the Agent subsystem.

Previous phases established:

```text
Agent domain
    ↓
IAgentRuntime
    ↓
HermesAgentRuntime
    ↓
Agent Tools
    ↓
Agent Memory
    ↓
AgentOrchestrator
    ↓
AgentRunWorker
    ↓
Agent API
    ↓
Metrics / Observability
```

However, the real Hermes execution path has not yet been empirically verified.

The primary objective of this phase is therefore:

> Prove that a real persisted `AgentRun` can travel through the complete Factory execution pipeline and successfully execute against a real Hermes environment.

The second objective is to establish a deterministic **stability gate** before adding more advanced agent capabilities.

---

# 1. Repository Inspection

Before modifying anything:

1. Read `AGENTS.md`.
2. Inspect the complete current Agent subsystem.
3. Inspect:
   - `AgentRun`
   - `AgentDefinition`
   - `AgentRunStatus`
   - `IAgentRunRepository`
   - `AgentRunRepository`
   - `IAgentOrchestrator`
   - `AgentOrchestrator`
   - `AgentRunWorker`
   - `IAgentRuntime`
   - `HermesAgentRuntime`
   - `HermesAgentExecutor`
   - `IAgentExecutor`
   - `IAgentToolRegistry`
   - `ReadArtifactTool`
   - `IAgentMemory`
   - `LocalAgentMemoryProvider`
   - Agent API endpoints/services
   - operational metrics
   - Worker configuration
   - Docker configuration
   - existing integration/E2E test infrastructure
4. Inspect the existing Hermes configuration and Docker setup.
5. Inspect `.ai/context/phase-18.1.1-*` through `.ai/context/phase-18.1.6-*`.
6. Search for any existing Hermes smoke/integration tests before creating new infrastructure.

Do not duplicate an existing E2E/test harness.

---

# 2. Baseline

Treat Phase 18.1.6 as complete.

Do not redesign:

- Agent API;
- AgentOrchestrator;
- AgentRunWorker;
- HermesAgentRuntime;
- tool registry;
- memory abstraction;
- metrics abstraction;
- AgentRun lifecycle.

Do not redo previous implementation work.

The previous phase established:

```text
Full regression: 833/833 passed
```

Use the current repository state as the baseline.

---

# 3. Critical Goal — Real Hermes Execution

This phase must attempt a genuine execution using:

```text
Real AgentRun
    ↓
Real AgentRunWorker
    ↓
Real AgentOrchestrator
    ↓
Real HermesAgentRuntime
    ↓
Real HermesAgentExecutor
    ↓
Real Hermes process/container
```

Do not replace any component with a fake for the primary smoke test.

The only acceptable fake boundary is where the environment genuinely cannot provide an external dependency and that limitation must be explicitly documented.

---

# 4. Hermes Environment

Inspect the repository and existing environment first.

Determine the project's intended Hermes execution mechanism.

Do not assume:

- a hard-coded executable path;
- a specific Docker container name;
- a specific host filesystem path;
- a specific model;
- a specific API key;
- a specific provider.

Use the existing configuration abstractions.

The test must use the configured Hermes runtime boundary.

---

# 5. Real Hermes Smoke Test

Create a minimal smoke test that proves:

```text
Factory starts
    ↓
AgentRun is created/queued
    ↓
Worker claims AgentRun
    ↓
Orchestrator starts
    ↓
HermesAgentRuntime invoked
    ↓
Hermes actually executes
    ↓
result returns
    ↓
AgentRun becomes Succeeded
```

The prompt sent to Hermes must be deterministic and trivial.

Example concept:

```text
Return exactly the text:
AI_FACTORY_HERMES_SMOKE_OK
```

Do not use a complex coding task.

Do not ask Hermes to modify the repository.

Do not allow the smoke test to modify production source files.

---

# 6. Smoke Test Isolation

The real Hermes smoke test must run in an isolated environment.

It must not:

- modify the main source tree;
- modify production database;
- use production credentials;
- write outside the configured test workspace;
- alter the user's real Hermes state unexpectedly;
- depend on previous conversation history;
- depend on an existing Hermes session;
- depend on OpenViking state.

Prefer:

```text
temporary test workspace
temporary SQLite database
isolated Hermes data/state
```

where supported by the current architecture.

Clean up temporary resources after execution.

---

# 7. Credentials / Secrets

Never commit credentials.

Never place secrets in:

- source code;
- test files;
- `AgentRun.Input`;
- logs;
- documentation;
- screenshots;
- Git history.

If Hermes requires an API key:

- read it from the configured environment/secret mechanism;
- do not print it;
- do not persist it in the test database.

If the environment has no valid credentials/configuration, report:

```text
Real Hermes E2E: BLOCKED — environment configuration unavailable
```

Do not fake a PASS.

---

# 8. Model Selection

Use the existing configured model/provider unless the repository explicitly defines a dedicated test model.

The smoke test must prioritize:

1. deterministic behavior;
2. low cost;
3. short execution time;
4. reliable availability.

Do not redesign OpenRouter/model routing in this phase.

Do not introduce a new provider abstraction merely for the smoke test.

---

# 9. Real Tool Verification

The smoke test should also verify that the runtime can see the Factory's tool boundary where practical.

At minimum verify that:

```text
IAgentToolRegistry
```

is resolved successfully.

If the current Hermes integration already supports passing tool descriptors into Hermes, verify the descriptor path.

Do not implement a new Hermes tool protocol in this phase.

Do not add shell/file-write tools.

The primary smoke remains a minimal safe execution.

---

# 10. Memory Verification

Verify that the Agent execution path can access:

```text
IAgentMemory
```

without directly coupling Hermes to SQLite.

A minimal deterministic memory test may:

1. store a synthetic memory entry;
2. create an AgentRun scoped to the same project/agent;
3. execute the orchestrator;
4. verify the memory lookup occurs;
5. verify execution remains successful.

Do not require semantic/vector search.

Do not integrate OpenViking yet.

The goal is boundary verification, not advanced memory intelligence.

---

# 11. API-to-Hermes E2E

Where practical, add one end-to-end scenario:

```text
HTTP POST /api/agents/runs
        ↓
AgentRun = Queued
        ↓
real Worker
        ↓
real Orchestrator
        ↓
real Hermes
        ↓
AgentRun = Succeeded
        ↓
HTTP GET /api/agents/runs/{id}
        ↓
result available
```

This is the strongest validation of the current architecture.

If the existing test host cannot safely run the Worker and real Hermes together, create a dedicated E2E harness rather than contaminating ordinary API tests.

---

# 12. Failure E2E

Verify at least one real failure path.

The failure must be controlled and safe.

Examples:

- deliberately invalid Hermes configuration;
- unavailable runtime endpoint in an isolated test configuration;
- deterministic runtime timeout.

The expected result must be:

```text
AgentRun → Failed
```

or:

```text
AgentRun → TimedOut
```

according to the actual failure category.

The Worker must remain alive after the failure.

Do not intentionally cause destructive OS/process failures.

---

# 13. Cancellation E2E

If the current Hermes/runtime configuration allows reliable cancellation testing:

1. start a deliberately long-running test AgentRun;
2. cancel it;
3. verify the final state;
4. verify no orphaned Worker execution remains.

If the real Hermes environment cannot provide a deterministic cancellation scenario, do not manufacture one.

Report:

```text
Real cancellation E2E: NOT VERIFIED
```

while retaining deterministic cancellation tests from earlier phases.

---

# 14. Worker Recovery

Verify:

```text
AgentRun A fails
        ↓
Worker remains alive
        ↓
AgentRun B is still executable
```

This can be done with:

- one controlled failing run;
- followed by one successful run.

The second run must prove that Worker failure isolation works with the real runtime path.

---

# 15. Persistence Verification

After real execution, verify the database state.

At minimum:

```text
AgentRun.Status
AgentRun timestamps
AgentRun result
AgentRun failure category when applicable
AgentRun tool information
```

must match the actual execution.

Do not inspect Hermes internal SQLite as Factory workflow truth.

The Factory database remains authoritative for AgentRun lifecycle.

---

# 16. Metrics Verification

Verify that real execution updates the Agent metrics introduced in Phase 18.1.6.

At minimum:

```text
created
started
succeeded
failed
timed out
cancelled
worker claim success
duration
```

where applicable.

Do not assert exact metric values globally if the test environment contains other activity.

Use a fresh metrics instance or bounded before/after deltas.

---

# 17. Logging Verification

Inspect real execution logs and verify that the lifecycle can be correlated:

```text
AgentRun created
AgentRun claimed
AgentRun started
Hermes execution
AgentRun completed
```

Do not log or expose secrets.

Do not store full sensitive prompts in normal operational logs.

---

# 18. Timeout / Resilience Verification

Verify that the existing runtime resilience layer behaves correctly with the real Hermes boundary.

At minimum establish one controlled timeout/failure scenario if the environment permits.

Do not redesign resilience.

Do not introduce a new retry framework.

Do not duplicate `Microsoft.Extensions.Http.Resilience` or existing retry infrastructure.

The objective is validation, not architecture changes.

---

# 19. Stability Test

After the single smoke test passes, execute a small repeated deterministic run.

Recommended:

```text
5 consecutive real AgentRun executions
```

using the same minimal smoke prompt.

For each execution record:

```text
run id
start time
duration
final status
failure category
result marker
```

Success criterion:

```text
5/5 Succeeded
```

with the expected result marker.

If real Hermes cost/availability makes five executions unreasonable, use the maximum safe number possible and document the limitation.

Do not hide intermittent failures.

---

# 20. Regression Stability

After real E2E validation:

Run the normal deterministic test suite once:

```text
dotnet test apps/AI.Factory.slnx
```

The existing regression baseline is 833 tests.

The new test total must be reported from the actual run.

Do not claim the previous number if tests have changed.

---

# 21. Build Validation

Run:

```text
dotnet build apps/AI.Factory.slnx
```

and Release:

```text
dotnet build apps/AI.Factory.slnx -c Release
```

Requirements:

```text
0 warnings
0 errors
```

---

# 22. Database Validation

If this phase requires no schema changes:

```text
NO MIGRATION
```

If an E2E harness requires a temporary database, use an isolated temporary database.

Do not alter existing migrations merely to support testing.

If a schema change is genuinely required:

- add an additive migration;
- validate clean migration;
- do not rewrite previous migrations.

---

# 23. Docker Validation

If Hermes is containerized in the existing project environment, verify:

```text
Factory container
    ↓
Worker container/process
    ↓
Hermes container/process
```

only to the extent supported by the existing architecture.

Do not redesign Docker Compose.

Do not change production deployment topology solely for this test.

If a small test-only Docker configuration is required, keep it isolated and document it.

---

# 24. No-Goals

Do NOT implement:

- OpenViking integration;
- multi-agent delegation;
- agent teams;
- DAG execution;
- autonomous task planning;
- code-generation agent;
- shell agent;
- browser agent;
- Git agent;
- autonomous software factory loop;
- new LLM provider;
- new model-routing architecture;
- dashboard;
- frontend;
- distributed Worker;
- horizontal scaling;
- advanced scheduling;
- production Kubernetes architecture;
- major CI/CD changes.

This phase is a validation/stability gate.

---

# 25. Architecture Freeze

Unless an actual blocking defect is discovered, treat the following boundaries as frozen:

```text
AgentRun
    ↓
IAgentOrchestrator
    ↓
IAgentRuntime
    ↓
HermesAgentRuntime
    ↓
IAgentExecutor
```

and:

```text
IAgentToolRegistry
IAgentMemory
```

must remain interface boundaries.

Do not refactor these components merely for stylistic reasons.

If a real E2E failure exposes a design defect, fix the smallest root cause and add a regression test.

---

# 26. Bug Handling

If E2E reveals a real defect:

1. reproduce it;
2. identify the smallest root cause;
3. fix it;
4. add a deterministic regression test;
5. rerun the affected E2E scenario;
6. rerun the full regression once after stabilization.

Do not hide or workaround failures merely to obtain a GO.

---

# 27. Documentation

Create:

```text
.ai/context/phase-18.1.7-agent-e2e-stability.md
```

Document:

1. actual E2E architecture;
2. Hermes environment;
3. configuration method;
4. test workspace isolation;
5. smoke prompt;
6. actual execution result;
7. failure-path validation;
8. cancellation result;
9. Worker recovery result;
10. persistence verification;
11. metrics verification;
12. logging/correlation verification;
13. stability-run results;
14. test/build results;
15. database status;
16. known limitations;
17. exact Real Hermes E2E status;
18. final GO/NO-GO decision.

Never put credentials or secrets in this document.

---

# 28. Evidence Table

The final report must include an empirical evidence table similar to:

```text
| Gate | Result | Evidence |
|------|--------|----------|
| Real Hermes smoke | PASS/FAIL | actual run ID/result |
| API → Worker → Hermes | PASS/FAIL | actual execution |
| Failure isolation | PASS/FAIL | actual run sequence |
| Persistence | PASS/FAIL | actual DB verification |
| Metrics | PASS/FAIL | actual metric evidence |
| Logging/correlation | PASS/FAIL | actual logs |
| Stability 5x | PASS/FAIL | actual 5-run results |
| Full regression | PASS/FAIL | actual test output |
| Debug build | PASS/FAIL | actual build output |
| Release build | PASS/FAIL | actual build output |
```

Never fill this table with assumptions.

---

# 29. Real Hermes Gate

This is the most important gate of this phase.

The final report must explicitly state one of:

```text
Real Hermes E2E: VERIFIED
```

or:

```text
Real Hermes E2E: NOT VERIFIED
```

If blocked by environment:

```text
Real Hermes E2E: BLOCKED
Reason: ...
```

Do not call the phase fully validated if the real Hermes gate remains unverified.

---

# 30. GO / NO-GO Rules

## GO

Only if:

- real Hermes execution succeeds;
- Factory AgentRun reaches `Succeeded`;
- result is returned correctly;
- Worker remains healthy;
- persistence is correct;
- metrics are updated;
- logs are correlatable;
- controlled failure is handled correctly;
- stability run passes;
- full deterministic regression passes;
- Debug build passes;
- Release build passes;
- no unresolved architectural defect exists.

## NO-GO

If:

- real Hermes cannot execute;
- Worker cannot successfully execute a real AgentRun;
- AgentRun lifecycle becomes inconsistent;
- result is lost;
- Worker dies after AgentRun failure;
- secrets leak;
- persistence is incorrect;
- repeated real execution is unstable;
- existing regression fails without an accepted root-cause fix;
- Debug/Release build fails;
- a critical architecture boundary must be bypassed to make Hermes work.

---

# 31. Final Report

Provide:

## E2E Architecture

Show the actual path:

```text
API
 ↓
AgentRun
 ↓
Worker
 ↓
Orchestrator
 ↓
HermesAgentRuntime
 ↓
Hermes
```

## Hermes Environment

State exactly how Hermes was executed.

## Smoke Test

Report:

```text
Prompt/result marker:
AgentRunId:
Final status:
Duration:
Result:
```

## Failure Test

Report actual result.

## Cancellation

Report:

```text
VERIFIED
```

or:

```text
NOT VERIFIED
```

## Worker Recovery

Report actual sequence:

```text
Run A:
Run B:
Worker remained alive: YES/NO
```

## Stability Run

Report actual results:

```text
Run 1:
Run 2:
Run 3:
Run 4:
Run 5:
```

## Metrics

Report actual verified metrics.

## Persistence

Report actual database verification.

## Tests

```text
Targeted E2E: X/X
Regression: X/X
```

## Build

```text
Debug:   ...
Release: ...
```

## Database

```text
Migration required: YES/NO
```

## Real Hermes

Exactly one:

```text
Real Hermes E2E: VERIFIED
```

or:

```text
Real Hermes E2E: NOT VERIFIED
```

or:

```text
Real Hermes E2E: BLOCKED
```

## Problems

List every real defect discovered and the corresponding fix.

## Final Decision

Exactly one:

```text
GO
```

or:

```text
NO-GO
```

## Next Phase

If GO, the next phase should move beyond infrastructure validation toward the first meaningful **agentic workflow**, beginning with controlled task execution and structured AgentRun input/output rather than immediately implementing unrestricted autonomous coding.

Do not begin that next phase until this stability gate has passed.