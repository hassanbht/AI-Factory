# AI Factory — Product Context

## Mission

AI Factory is an autonomous software-engineering platform. It orchestrates:

```
Requirements → Specification → Architecture → Planning → Implementation
→ Build → Test → Repair → Review → Human Approval
```

The LLM is a worker, not the decision-maker. The Orchestrator (state machine +
DAG + validation + review) controls the process. SQLite holds the
authoritative workflow state; Git/filesystem holds source code and
artifacts; Hermes executes agent work; OpenRouter is the model gateway;
OpenViking (future) is a knowledge/retrieval layer only.

## Target end-to-end flow

```
                   USER
                     │
                     ▼
              Product Idea
                     │
                     ▼
              Requirements
                     │
                     ▼
             Multi-Agent Analysis
          ┌──────────┼──────────┐
          ▼          ▼          ▼
       Claude      GPT       Gemini
          │          │          │
          └──────────┼──────────┘
                     ▼
                Consensus
                     │
                     ▼
               Specification
                     │
                     ▼
                Architecture
                     │
                     ▼
                Task Planner
                     │
                     ▼
                  Task DAG
                     │
                     ▼
              ┌──── Orchestrator
              │
              ▼
          Hermes Agent
              │
              ▼
             Code
              │
              ▼
        Build + Test
          │       │
        FAIL     PASS
          │       │
          ▼       ▼
        Repair   Review
          │       │
          └───┐   │
              ▼   ▼
             Re-test
                │
                ▼
          Independent Review
                │
                ▼
          Human Approval
                │
                ▼
             COMPLETE
```

## Current priority

Build the reliable orchestration core first (Domain → Persistence → Workflow
Engine → Task DAG). Do not optimize for maximum autonomy before reliability.
Do not add OpenViking, learning, multi-agent optimization, or autonomous
production deployment until the workflow engine is stable.

## Non-goals (for now)

- No parallel task execution (concurrency = 1 until reliability is proven).
- No vector search / embeddings until the Context Engine phase.
- No automatic prompt/system-behavior mutation from a single failure.
- No blind trust in LLM output — every completed task is evaluated before
  anything is learned from it.

See [architecture.md](architecture.md) for layering rules,
[workflow.md](workflow.md) for the per-task execution sequence, and
[decisions.md](decisions.md) for the running decision log.
