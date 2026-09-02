# Implement AI Factory Evaluation Layer

Implement an evaluation layer. The system must NOT blindly learn from
every agent output.

For every completed task evaluate: build result, test result, review
result, human approval if applicable, repair count, regression status.

Create:

- `IEvaluationEngine`
- `EvaluationResult`

See [16-learning.md](16-learning.md) for what consumes the evaluation
result — that is a separate phase, do not implement it here.
