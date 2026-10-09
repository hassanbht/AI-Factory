# Phase Q Certification Rebaseline Decision

Decision ID: `PhaseQ-Rebaseline-2026-10-08-01`  
Certification epoch: `PhaseQ-Certification-Epoch-2`  
Decision date: 2026-10-08

## Approval

The user explicitly approved creation of a new certification epoch in response to the Phase Q.1 governance question. This file is the repository-visible approval marker:

```text
PHASE_Q_REBASELINE_APPROVED=true
```

This approval authorizes **rebaselining the evidence ledger only**. It does not certify Phase Q, authorize Phase R, or waive any execution, security, regression, provider-readiness, acceptance, cleanup, or cost requirements. It does not itself authorize treating estimated or unknown cost as reported cost.

## Rationale and evidence

- Historical `TestResults/PhaseQ/manifest.json` and its raw run files are retained unchanged as the legacy epoch.
- Historical Q-A-2 attempt 1 (`AgentRunId=08aaf881-0576-4616-a608-943aed3be171`) timed out with no usage, OpenRouter generation ID, provider request ID, or cost in preserved evidence.
- The supported OpenRouter generation lookup requires a provider `gen-...` generation ID. The retained timeout evidence contains no such ID or matching provider request ID; Hermes records only its own estimates for the successful Q-A-2 retry. Those estimates are not authoritative billing evidence.
- No account-side OpenRouter Logs export or other provider-side reconciliation record was provided. The historical unknown amount therefore remains UNKNOWN and outside the new epoch's certified-spend ledger; it is not converted to zero.
- The separate 402 remediation smoke remains separate, `BLOCKED / BILLING / UNKNOWN`, and outside both the historical Q-A sample set and the new certification epoch.

## New-epoch limits

`PhaseQ-Certification-Epoch-2` has exactly twelve required identities. All begin `NOT_RUN`; no historical result is carried forward or certified. Each live result must be recorded only in this epoch's manifest and raw evidence directory.

Before any billed epoch run, the runner must prove:

1. the normal build and three-run default regression gate pass;
2. provider readiness including sufficient credits and the exact Hermes/model route;
3. actual provider cost can be authoritatively captured and associated with that AgentRun;
4. the $2.00 epoch ceiling can be enforced from authoritative reported cost;
5. all other per-run acceptance, integrity, security, and cleanup requirements pass.

If any prerequisite is unavailable, no model call may be made. In particular, Hermes-reported `estimated_cost_usd`, an HTTP 402 response, missing usage, elapsed time, or token estimates do not establish provider-billed cost.

## Final status

The rebaseline is approved, but the certification epoch is not started or certified. The current provider evidence shows HTTP 402 insufficient credits, and authoritative per-generation cost capture is not configured/proven. Phase Q remains `LIMITED — EXTERNAL RUNTIME CONSTRAINT`; Phase R remains prohibited.
