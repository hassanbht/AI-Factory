# Implement AI Factory Learning Layer

Builds on [15-evaluation.md](15-evaluation.md) — the evaluation layer must
already exist.

Create:

- `ILearningStore`
- `Lesson`

A lesson must contain: project, task type, problem, solution, evidence,
confidence, source, createdAt.

Only store lessons when evidence supports them. Never store an LLM
hallucination as a verified lesson.

Separate knowledge levels: `Observed`, `Inferred`, `Verified`. Verified
lessons require successful validation.

The learning system must be retrievable by future `IContextProvider`
implementations.

Do not automatically modify system prompts based on a single failure.
Learning must be incremental and auditable.
