# Implement Independent Code Review

Implement an independent review stage.

The reviewer must receive:

* requirements
* acceptance criteria
* architecture
* changed files
* tests
* build results
* task history

The reviewer must evaluate:

1. correctness
2. requirements compliance
3. architecture
4. security
5. error handling
6. test coverage
7. maintainability
8. unnecessary complexity

The reviewer must return structured findings:

* severity
* category
* file
* line if known
* description
* recommendation

Severity:

Critical
High
Medium
Low
Info

Rules:

Critical or High findings prevent completion.

Medium findings may require repair according to policy.

Low and Info findings do not automatically block completion.

The review must use a model/agent independent from the implementation agent whenever possible.

Do not allow an implementation agent to automatically mark its own implementation as reviewed.
