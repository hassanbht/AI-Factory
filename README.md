# AI Factory

> A self-hosted control plane for structured, human-governed AI software development.

![Project status](https://img.shields.io/badge/status-experimental-orange)
![Backend](https://img.shields.io/badge/backend-C%23%20%2F%20.NET-512BD4)
![Client](https://img.shields.io/badge/client-Flutter-02569B)
![Execution](https://img.shields.io/badge/execution-Docker-2496ED)

AI Factory is an experimental platform for coordinating AI coding agents through a controlled software-delivery workflow. Instead of allowing an agent runtime to own the entire process, AI Factory keeps project state, task orchestration, verification, review, and human approval under the Factory's control.

The goal is to make AI-assisted development more observable, testable, resumable, and easier to govern—not simply to send a prompt to a coding model and trust its claim that the work is finished.

**Project status:** Active development. Interfaces, runtime integrations, setup instructions, and operational requirements may change. This project should not be treated as production-ready without an independent review of the current code, configuration, and security model.

## Why AI Factory?

AI coding tools are useful, but reliable software delivery needs more than code generation. It also needs task planning, dependency management, execution tracking, verification, failure handling, review, and a clear definition of who is allowed to approve a result.

AI Factory is designed around that separation:

- **The Factory owns the workflow.** Project and task lifecycle, scheduling, execution records, acceptance, audit, and approval remain in the control plane.
- **Runtimes are replaceable.** Agent-specific behavior belongs behind execution adapters instead of being spread throughout business logic.
- **Success must be verified.** An agent's response or process exit code is not, by itself, proof that a task meets its acceptance criteria.
- **Human approval stays explicit.** Where the workflow requires approval, an agent cannot approve its own work or silently mark the project complete.
- **Execution should be observable and recoverable.** Runs need clear states, useful diagnostics, bounded execution, and safe cleanup.

## Key capabilities

The repository is under active development. The following describes the system's design and implementation direction; the maturity of individual paths depends on the current branch and configuration.

- **Project lifecycle management** for tracking software work from planning through execution, verification, review, and approval.
- **Work items and dependencies** to represent tasks and the order in which they can run.
- **Agent-run orchestration** with execution tracking and handling for outcomes such as success, failure, timeout, and cancellation.
- **Pluggable execution architecture** intended to separate the Factory's business workflow from the coding-agent runtime.
- **Docker-based execution** for running supported agent workloads in dedicated execution containers, with attention to lifecycle cleanup and workspace boundaries.
- **Model and gateway configuration** kept separate from the runtime adapter where practical.
- **Factory-owned verification and acceptance** so that runtime output is checked by the delivery workflow rather than accepted solely on the agent's word.
- **Review and human approval** as distinct stages of the delivery lifecycle.
- **Operational diagnostics and audit information** to help investigate execution outcomes and failures.
- **Flutter client** for interacting with the Factory through its backend API.

## Architecture

At a high level, the intended boundary is:

```text
Flutter Client
      |
      v
ASP.NET Core API
      |
      v
AI Factory Control Plane
  - Projects and lifecycle
  - Work items and dependencies
  - Agent runs and orchestration
  - Verification and acceptance
  - Review and human approval
  - Audit and diagnostics
      |
      v
Execution Dispatcher / Adapter Layer
      |
      +--> Hermes
      +--> OpenHands
      +--> Claude Code
      +--> Codex
      +--> Additional runtimes (future/evaluation)
      |
      v
Model Provider / Gateway
  - Configuration-dependent
  - For example: OpenRouter or OmniRoute
```

The diagram describes the architectural boundary, **not a guarantee that every runtime is enabled or production-certified**. Integration maturity, authentication, provider availability, model access, and local configuration vary. Check the current implementation and configuration before selecting a runtime.

### Core design rule

Agent runtimes may produce code, logs, summaries, and artifacts. They do not own the authoritative Factory project lifecycle, acceptance decision, or human-approval policy. Those decisions remain with AI Factory.

## Technology

The project uses the following technologies in its current architecture:

- **Backend:** C# / .NET and ASP.NET Core
- **Client:** Flutter / Dart
- **Execution:** Docker-based agent workloads
- **AI execution:** Adapter-based integration with coding-agent runtimes
- **Model access:** Configurable model/provider gateway integration
- **Testing:** .NET unit and integration test suites, including execution-lifecycle tests

Exact SDK versions and runtime requirements should be taken from the repository's project files and configuration. They may change as development continues.

## Getting started

AI Factory is a developer-oriented project, and a successful start depends on the selected runtime, model provider, credentials, and local Docker setup. Review the repository configuration before launching any services.

### Prerequisites

- Git
- A .NET SDK compatible with the solution's target framework
- Docker Desktop on Windows, or Docker Engine on Linux
- Flutter SDK if you intend to build or run the client
- Credentials and access for the model provider and execution runtime you configure

### Clone the repository

```bash
git clone https://github.com/hassanbht/AI-Factory.git
cd AI-Factory
```

### Run the backend tests

From the repository root:

```bash
dotnet test apps/AI.Factory.slnx
```

This command runs the .NET solution's test suite. Tests that require Docker, external services, or live provider credentials may have additional environment requirements; consult the individual test names and configuration before running them.

### Configure and run the services

1. Review the solution projects, local development settings, and Docker/Compose files in the repository.
2. Configure the required execution runtime and model gateway explicitly.
3. Supply secrets through the intended local secret/environment configuration. Do not commit API keys or provider credentials.
4. Ensure Docker is available and that the configured runtime can access only the intended workspace and resources.
5. Start the API, Worker, and Flutter client using the development scripts and instructions present in the current checkout.

The local startup process is configuration-dependent and may evolve during development. Do not expose the API, an agent runtime, or Docker control endpoints to an untrusted network without adding and validating the required authentication and isolation controls.

## Runtime and provider support

AI Factory is moving toward an execution-adapter model. Runtime support should be understood as **experimental and configuration-dependent**, not as a promise that every runtime works in every environment.

- **Hermes:** Existing execution path and Docker-based runtime integration.
- **OpenHands:** Experimental integration path; verify the current adapter, server version, and configuration before use.
- **Claude Code and Codex:** Experimental integration paths; execution and credential isolation depend on the configured environment.
- **Other runtimes:** Additional adapters may be evaluated as the architecture develops. Do not assume support unless it is implemented and verified in the current checkout.

Model gateways such as OpenRouter and OmniRoute are provider infrastructure, not execution adapters. A configured gateway does not guarantee that a particular account, model, quota, or endpoint is available.

## Security notes

AI Factory orchestrates software agents that may execute commands and read or modify files. Treat runtime configuration and workspace isolation as security-sensitive.

- Never commit API keys, access tokens, passwords, or private configuration.
- Keep provider credentials on the backend/runtime side; never embed them in the Flutter client.
- Use a disposable development environment and test database for experiments.
- Restrict Docker access and agent workspaces. Do not mount sensitive host directories unless absolutely necessary.
- Do not expose an agent server or execution endpoint publicly without verifying authentication, authorization, network restrictions, and resource limits.
- Review logs and diagnostic output for accidental secret disclosure before sharing them.
- Independently review the current code and deployment configuration before using the project with important repositories or production credentials.

## Development principles

Contributions and future changes should preserve these boundaries:

1. Keep project and WorkItem lifecycle logic in AI Factory rather than in a runtime adapter.
2. Keep provider-specific and runtime-specific details behind well-defined interfaces.
3. Preserve existing behavior when introducing an adapter or changing execution infrastructure.
4. Test normal completion, failures, timeouts, cancellation, and cleanup—not only the happy path.
5. Treat acceptance as a Factory-owned verification step.
6. Require explicit human approval wherever the workflow calls for it.
7. Make claims about live runtime support only when they have been verified against the current source and environment.

## Current limitations

- The project is experimental and is still evolving.
- Setup and configuration can require familiarity with .NET, Flutter, Docker, and model-provider credentials.
- Runtime compatibility and live model availability vary by environment.
- Passing automated tests does not replace a security review or prove that every external runtime/provider is available.
- Do not use this repository as an unattended production deployment system without performing your own risk assessment and validation.

## Contributing

Issues, bug reports, and focused improvement proposals are useful. When reporting a problem, include the relevant environment details, the exact command or workflow used, and sanitized logs or test output. **Never include secrets, private source code, or personal data in an issue.**

Before submitting a pull request, run the relevant tests and describe what was verified. For runtime integrations, distinguish unit-test evidence from real Docker or live-provider validation.

## License

See the repository's `LICENSE` file for the applicable terms. If the repository does not yet contain a license file, add one before presenting the project as open source. Public visibility alone does not automatically grant permission to reuse, modify, or redistribute the code.

---

**Repository:** [hassanbht/AI-Factory](https://github.com/hassanbht/AI-Factory)
