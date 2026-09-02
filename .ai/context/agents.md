# AI Factory — Agent Usage Context

## Which tool for which work

**Claude Code** — architecture, refactoring, complex implementation,
review, debugging, understanding a large codebase. Works the
gather-context → action → verify loop; reads `CLAUDE.md` automatically at
session start, supports Skills/Subagents/Hooks for the repeating
per-phase workflow.

**Codex** — implementation, refactoring, running tests, fixing build
failures, repository-wide changes. Its CLI is built for direct
repository work.

Neither tool gets the whole system in one prompt. One phase, one prompt
file, one session.

## Invocation pattern

```powershell
cd D:\Project\AI-Factory
claude
```
```
Read .ai/prompts/03-workflow-engine.md and implement exactly that phase.
```

Same pattern for `codex`. The prompt file itself carries the
"read AGENTS.md / CLAUDE.md / .ai/context/* / .ai/state/implementation-status.md
first, inspect before implementing" instructions — see
[workflow.md](workflow.md).

## Provider abstraction reminder

`IAgentExecutor`, `IModelRouter`, `IContextProvider`, `IWorkflowEngine` are
the only points where Hermes/OpenRouter/OpenViking may be reached from
Application code. Never hard-code a specific model or provider in business
logic — see `AGENTS.md` § "AI provider abstraction" and
[architecture.md](architecture.md).

## Multi-agent review (future phase)

Independent Code Review (`.ai/prompts/12-independent-review.md`) must use a
model/agent independent from the implementation agent whenever possible —
an agent must never mark its own implementation as reviewed.
