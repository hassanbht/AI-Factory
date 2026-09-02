# Implement Repair Loop

Builds on [10-build-test.md](10-build-test.md) — the validation pipeline
must already exist.

When validation fails, create a `RepairContext` containing: task, changed
files, compiler errors, test failures, relevant source, previous attempt
output, previous repair attempts.

Repair must be bounded. Maximum attempts must be configurable. After
maximum attempts, transition to `HumanReview` or `Failed` according to
policy.

Do not silently continue after repeated failures.

All validation runs must be persisted.

Create tests with fake build/test executors. Do not require a real
compiler in unit tests.
