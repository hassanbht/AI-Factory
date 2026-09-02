# AI Factory — Development Workflow Context

## Before changing any file

1. Inspect the relevant repository files.
2. Identify existing implementations.
3. Identify dependencies.
4. Identify risks.
5. Produce a concise implementation plan.
6. Check the plan against `AGENTS.md`.
7. Only then modify files.

Do not ask the user to repeat information already present in the
repository. Do not implement future phases. Do not make speculative
improvements. Implement only the requested scope.

## Required sequence for every implementation task

```
1. Understand
2. Plan
3. Implement
4. Build
5. Test
6. Inspect failures
7. Repair
8. Rebuild
9. Retest
10. Summarize
```

Never report success without actually running the relevant validation
commands. If validation fails, diagnose and repair before reporting
completion.

## How prompts are delivered

Never paste a multi-thousand-line prompt into a session. Each phase has a
single file under `.ai/prompts/`. Tell the agent:

```
Read .ai/prompts/<NN>-<name>.md and implement exactly that phase.
```

The agent reads `.ai/context/*.md` and `.ai/state/implementation-status.md`
as part of that prompt's own instructions — it does not need the context
repeated in the chat message.

## Definition of done

A task is not complete merely because code was written. It is complete
only when:

1. implementation exists;
2. build succeeds;
3. relevant tests pass;
4. architecture remains valid (see `architecture.md`);
5. no unrelated files were modified;
6. errors are handled;
7. logging exists where needed;
8. documentation is updated if behavior changed;
9. `.ai/state/implementation-status.md` is updated for work actually
   completed and validated — never mark an item done pre-emptively.

## Agent report format

```
## Implementation
## Files
## Validation
## Problems
## Next Step
```

Do not claim something was tested if it was not tested.
