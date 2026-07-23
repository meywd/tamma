# Story 41-19: Threat Modeling Workflow

Status: drafted

## User Story

As a **security** engineer (or eligible role-holder), I want a workflow that produces a typed `ThreatModel`
for a feature or system surface on the lifecycle — assets, threats, mitigations, residual risk — so that
security design is explicit, reviewed, and accepted before implementation, instead of an afterthought.

## Priority

P3 / Wave 3 — proactive security; naturally paired with 41-10 system design.

## Scope

Thin binding over `document-lifecycle`. `consumes: [Design (41-10)?, issue, context-scan, data-flow]` /
`produces: ThreatModel`. Produce cell `(security, threat-model)`.

## Produced document

`ThreatModel` (41-1): STRIDE (or configured) categorisation; each threat has asset + mitigation +
residual-risk; unmitigated high-risk ⇒ escalation. `issueId` lineage. Reviewed via security/architect lens.

## Events

`THREATMODEL.STARTED` → `.DRAFTED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; unmitigated high-risk threats always escalate regardless of dial and can
seed security tasks. Accepted model informs `plan-generation` and 41-15 verification.

## Autonomy behavior

- **70–84:** agent drafts; security accepts.
- **85–100:** agent drafts and self-accepts a fully-mitigated model; any unmitigated high-risk escalates.

## Acceptance Criteria

1. Thin lifecycle binding; `ThreatModel` validated (categorisation, mitigation+residual per threat).
2. Unmitigated high-risk cannot be accepted silently — it is a typed escalation.
3. Consumable by `plan-generation`/41-15 via 39-11.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** 41-1 (`ThreatModel` type), Epic 39 (lifecycle, review, store).
- **Related:** consumes 41-10.

## Estimated Effort

4 days
