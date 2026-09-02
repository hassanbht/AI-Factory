# Integrate OpenViking as Context Provider

Do not implement this until the full cycle below already works:

```
Project → Plan → Task DAG → Hermes → Code → Build → Test
→ Repair → Review → Human Approval
```

Do not redesign the AI Factory architecture.

Add OpenViking as an implementation of the existing `IContextProvider`
abstraction. The Application layer must remain unaware of OpenViking.

OpenViking must be used for long-lived contextual knowledge such as:
project documentation, architecture decisions, previous successful
implementations, recurring failures, coding patterns, technical knowledge,
validated solutions.

Do NOT store authoritative workflow state in OpenViking. Workflow state
remains in SQLite. Source code remains in Git/filesystem. OpenViking is a
retrieval/knowledge layer only.

Implement `OpenVikingContextProvider` with configuration-driven connection
settings. Support graceful fallback when OpenViking is unavailable. The
system must remain operational without OpenViking.

Create integration tests using a fake context provider and separate
OpenViking integration tests.

Do not couple domain entities to OpenViking.
