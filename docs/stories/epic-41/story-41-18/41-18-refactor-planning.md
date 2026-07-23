# Story 41-18: Refactor Planning Workflow

Status: drafted

## User Story

As a **senior developer** (or eligible role-holder), I want a workflow that turns a refactor need (from
41-11 tech-debt triage or a review concern) into a typed `Plan` on the lifecycle, so that refactors are
scoped, sequenced, and accepted with a behavior-preservation strategy before the coding step runs —
instead of an unbounded ad-hoc rewrite.

## Priority

P3 / Wave 3 — turns tech-debt findings into safe, planned work.

## Scope

Thin binding over `document-lifecycle`. `consumes: [tech-debt TriageDecision (41-11), context-scan, Review
concerns]` / `produces: Plan`. Produce cell `(senior_developer, plan-refactor)`.

## Produced document

`Plan` (39-4): per-step file map, dependency ordering, behavior-preservation/testing stated per step (the
`refactor` action's characterization-test requirement expressed as plan content). `repository`/`issueId`
lineage. Reviewed via panel.

## Events

`REFACTOR.PLAN.STARTED` → `.DRAFTED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; accepted plan hands off to the coding step (Epic 40) step-by-step. A
refactor touching a public API can be an always-escalate class.

## Autonomy behavior

- **70–84:** agent drafts; senior dev accepts before work.
- **85–100:** agent drafts and self-accepts contained refactors; API-affecting refactors always escalate.

## Acceptance Criteria

1. Thin lifecycle binding; `Plan` validated (ordering, per-step testing/behavior preservation).
2. Consumes 41-11 tech-debt output when present.
3. Accepted plan consumable by the coding step via 39-11.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (`Plan`, lifecycle, review-panel, store); Epic 40 for execution.
- **Related:** consumes 41-11.

## Estimated Effort

3–4 days
