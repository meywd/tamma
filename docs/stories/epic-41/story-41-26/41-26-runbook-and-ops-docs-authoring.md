# Story 41-26: Runbook & Ops-Docs Authoring Workflow

Status: drafted

## User Story

As a **devops** engineer / tech writer (or eligible role-holder), I want a workflow that authors an
operational runbook for a service or a recurring operational task, as audience-tagged prose on the
lifecycle, so that on-call procedures are captured, reviewed, and kept current instead of tribal knowledge.

## Priority

P3 / Wave 3 — proactive ops hygiene; naturally seeded by 41-22 postmortems.

## Scope

Triggered on demand or by a postmortem action item → thin binding over `document-lifecycle`. `consumes:
[service/infra context (context-scan), incident Diagnosis/postmortem?, deployment config]` / `produces:
prose (runbook, audience=ops)`. Produce cell `(devops, write-runbook)`; review via `(tech_writer,
review-docs)` or an ops peer.

## Produced document

Audience-tagged prose runbook (symptoms → checks → remediation → escalation), `repository`-lineaged.
Review stage is a `Review` over the text.

## Events

`RUNBOOK.STARTED`/`.DRAFTED`/`.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accepted runbook routes per autonomy and is published to the ops docs surface; a 41-22 postmortem
"add/update runbook" action item can dispatch this workflow directly.

## Autonomy behavior

- **70–84:** agent drafts; devops accepts.
- **85–100:** agent drafts and self-accepts; a runbook covering a regulated/critical path can be
  always-escalate.

## Acceptance Criteria

1. Thin lifecycle binding; prose reviewed by a `Review`.
2. Can be dispatched as a postmortem follow-up with the incident `Diagnosis` as input.
3. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (prose handling, lifecycle, review, store), `context-gathering`.
- **Related:** dispatched by 41-22.

## Estimated Effort

2–3 days
