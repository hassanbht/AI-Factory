# Implement Hermes Agent Executor

Implement the infrastructure adapter that executes a coding task through Hermes Agent.

Read:

* AGENTS.md
* CLAUDE.md
* .ai/context/agents.md
* .ai/state/implementation-status.md

## Architecture

Application owns:

IAgentExecutor

Infrastructure owns:

HermesAgentExecutor

Application must not know how Hermes is executed.

## Execution

Hermes currently runs through Docker.

The existing working pattern is:

docker run --rm
-v "${HOME}.hermes:/opt/data"
-v "D:\Project\AI-Factory\sandbox\agent-workspace:/workspace"
nousresearch/hermes-agent:latest
-z "<prompt>"

Do not assume this exact command should be hard-coded into business logic.

Create configuration:

HermesOptions

with:

* DockerImage
* DataVolume
* WorkspaceRoot
* TimeoutSeconds
* MaxOutputLength

Use dependency injection.

## Requirements

Implement:

IAgentExecutor

HermesAgentExecutor

HermesOptions

Hermes execution result mapping

The executor must:

* create an isolated task workspace
* execute Hermes
* capture stdout
* capture stderr
* capture exit code
* enforce timeout
* return structured result
* never expose secrets in logs
* never use infinite execution
* handle process failure
* handle timeout
* preserve output artifacts

## Security

Do not pass OpenRouter API keys through command arguments.

Do not log environment variables.

Do not use shell string concatenation where structured ProcessStartInfo arguments are possible.

## Tests

Create tests for:

* successful execution
* non-zero exit code
* timeout
* missing Docker
* malformed result
* cancellation

Where integration testing requires Docker, isolate it as an integration test.

Run build and tests.

Do not implement autonomous retry here.

Do not implement model routing here.
