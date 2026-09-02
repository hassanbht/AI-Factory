# Implement AI Factory Context Engine

Implement a provider-agnostic contextual information layer.

Create:

- `IContextProvider`
- `ContextQuery`
- `ContextDocument`
- `ContextResult`

The context system must be capable of retrieving: project requirements,
architecture documents, coding standards, previous task outputs, relevant
source files, test failures, previous agent results, project decisions.

Implement the first provider using the local filesystem and database. Do
NOT integrate OpenViking yet.

The provider must return structured context with: source, content,
relevance, metadata.

The context layer must support future replacement by OpenViking without
changing Application logic.

Create tests.

Do not implement embeddings yet.
Do not implement vector search yet.
Do not introduce unnecessary infrastructure.
