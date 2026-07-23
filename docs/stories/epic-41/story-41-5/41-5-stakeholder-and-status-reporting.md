# Story 41-5: Stakeholder & Status Reporting Workflow

Status: drafted

## User Story

As a **product owner / project manager** (or eligible role-holder), I want a scheduled workflow that
synthesizes progress against commitments from the DCB stream into an audience-tagged status update, so that
stakeholders get an accurate, evidence-backed report on a cadence without manual assembly.

## Priority

P2 / Wave 3 — recurring, cross-role; reuses the 41-7 event-read pattern at a stakeholder altitude.

## Scope

Scheduled trigger → thin binding over `document-lifecycle`. `consumes: [SprintPlan (41-6), DCB events for
the period, blocker/escalation events]` / `produces: prose (status-update, audience=stakeholder)`. Produce
cell `(product_owner, summarize-stakeholder)` (PM variant `(project_manager, report-status)` via 41-1).

## Produced document

Audience-tagged prose status update (accomplished / in-flight / at-risk / next), each claim traceable to
DCB evidence. `tenantId`/period lineage. Review stage is a `Review` over the text.

## Events

`STATUS_REPORT.STARTED`/`.DRAFTED`/`.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accepted report delivered via the orchestrator to the stakeholder audience; sensitive/external reporting
can be an always-escalate class (human sign-off before send).

## Autonomy behavior

- **70–84:** agent drafts; PO/PM accepts before send.
- **85–100:** agent drafts and self-accepts internal reports; external stakeholder reports per policy.

## Acceptance Criteria

1. Scheduled, tenant-scoped, idempotent per period; every claim cites DCB evidence.
2. Thin lifecycle binding; prose reviewed by a `Review`.
3. `[ResumeBehavior(Both)]` (scheduled + accept-gate); 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (prose handling, lifecycle, review, store, 4-7 query API), scheduler pattern.
- **Related:** consumes 41-6 SprintPlan.

## Estimated Effort

3–4 days
