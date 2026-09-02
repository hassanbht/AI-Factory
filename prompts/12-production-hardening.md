# Implement Human Approval Gate

Implement persistent human approval checkpoints.

The system must support:

PendingApproval
Approved
Rejected

Approval must be persisted.

A workflow paused for human approval must survive:

* API restart
* Worker restart
* Docker restart
* machine restart

The approval record must contain:

* project
* workflow
* reason
* requestedAt
* requestedBy
* decision
* decidedAt
* comment

The workflow must not continue automatically while approval is pending.

Create API endpoints for:

* get pending approvals
* approve
* reject

Create tests proving that the workflow remains paused until approval is persisted.
