# Story 41-21: Security Incident Analysis Workflow

Status: drafted

## User Story

As a **security** engineer (or eligible role-holder), I want a workflow that analyzes a security incident
into a typed `Diagnosis` on the lifecycle — ranked hypotheses, blast radius, affected assets, remediation
— so that security incidents get a rigorous, evidence-cited root-cause and tracked follow-ups, not an
ad-hoc scramble.

## Priority

P3 / Wave 3 — reactive security; sibling to devops 41-22.

## Scope

Reactive trigger (security alert / event) → thin binding over `document-lifecycle`. `consumes: [alert +
DCB events, audit Findings (41-20)?, affected surface context]` / `produces: Diagnosis`. Produce cell
`(security, analyze-security-incident)`.

## Produced document

`Diagnosis` (39-4): hypotheses ranked by confidence; fix/remediation references affected files/assets;
blast radius stated. `repository` lineage. Reviewed via security lens.

## Events

`SECURITY_INCIDENT.STARTED` → `.DIAGNOSED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; a confirmed active incident is an always-escalate class that pages the
security role and can trigger `rotate-secret` / a remediation plan; follow-ups route to owning roles.

## Autonomy behavior

- **70–84:** agent drafts the diagnosis; security accepts and directs response.
- **85–100:** agent diagnoses and self-accepts low-severity findings; active/high-severity always escalates.

## Acceptance Criteria

1. Thin lifecycle binding; `Diagnosis` validated (ranked hypotheses, remediation references).
2. Active-incident path always escalates and can dispatch `rotate-secret`/remediation.
3. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (`Diagnosis`, lifecycle, store, escalation); `rotate-secret`.
- **Related:** consumes 41-20; feeds 41-22 postmortem when cross-cutting.

## Estimated Effort

3–4 days
