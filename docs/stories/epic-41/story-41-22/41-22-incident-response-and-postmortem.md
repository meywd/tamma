# Story 41-22: Incident Response & Postmortem Workflow

Status: drafted

## User Story

As a **devops** engineer (or eligible role-holder), I want a workflow that runs an operational incident
from diagnosis through response to a written postmortem on the lifecycle, so that incidents produce a
tracked root-cause `Diagnosis`, coordinated response, and a blameless postmortem with action items —
instead of an untracked firefight.

## Priority

P3 / Wave 3 — high-consequence reactive ops; seeds 41-26 runbooks.

## Scope

Reactive trigger (alert / health-review escalation) → thin binding(s) over `document-lifecycle`, run as a
short sequence:
1. `produces: Diagnosis` — cell `(devops, diagnose-incident)`.
2. `produces: Plan` (response/rollback) — cells `(devops, plan-incident-response)` / `(devops, rollback)`.
3. `produces: prose (postmortem, audience=engineering)` — cell `(devops, write-postmortem)`.
`consumes: [alert, DCB deployment/health events, affected service context]`.

## Produced documents

`Diagnosis` (ranked hypotheses, affected files), `Plan` (response/rollback steps), and an audience-tagged
prose postmortem (timeline / root cause / impact / action items). `repository`/incident lineage.

## Events

`INCIDENT.STARTED` → `.DIAGNOSED` → `.RESPONSE_ACCEPTED` → `.RESOLVED` → `POSTMORTEM.ACCEPTED` alongside
`DOCUMENT.*`.

## Orchestrator / user interaction

Active incident is an always-escalate class that pages the devops role; the response plan's accept gate is
time-sensitive (bounded human window, then orchestrator-decides per policy). Postmortem action items route
to owning roles and can dispatch 41-26.

## Autonomy behavior

- **70–84:** agent diagnoses + drafts response; a human approves before executing rollback/response.
- **85–100:** agent may execute a pre-approved low-risk response class; destructive/prod rollback always
  escalates; postmortem drafted and human-accepted by default.

## Acceptance Criteria

1. Each stage is a thin lifecycle binding producing its typed/prose document; no bespoke terminals.
2. Active-incident always-escalate; destructive response always requires the configured decision.
3. Postmortem action items produce role-scoped Task View entries; can dispatch 41-26.
4. `[ResumeBehavior(Both)]` across the sequence; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (`Diagnosis`, `Plan`, prose, lifecycle, store, escalation), Epic 40 for any
  code/infra response step.
- **Related:** consumes 41-23 escalations; feeds 41-26; sibling to 41-21.

## Estimated Effort

5–6 days
