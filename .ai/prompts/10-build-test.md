# Implement Build/Test Validation Pipeline

Implement an isolated validation pipeline.

Create:

- `IBuildExecutor`
- `ITestExecutor`
- `IValidationEngine`
- `ValidationResult`

The pipeline must:

1. build the project;
2. capture stdout/stderr;
3. capture exit code;
4. run tests only when build succeeds;
5. capture test results;
6. normalize failures;
7. return structured diagnostics.

All validation runs must be persisted.

Create tests with fake build/test executors. Do not require a real
compiler in unit tests.

See [11-repair.md](11-repair.md) for what happens when validation fails —
that is a separate phase, do not implement it here.
