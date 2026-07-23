# Story 41-6: Sprint Planning Workflow

Status: drafted

## User Story

As a **scrum master / project manager** (or eligible role-holder), I want a workflow that commits a
capacity-bounded set of prioritised items to a time-box as a typed `SprintPlan` on the lifecycle, so that
sprint commitment is explicit, reviewed, and accepted — with owners and estimates — instead of decided in
an untracked meeting.

## Priority

P2 / Wave 3 — the agile-cadence anchor; consumes 41-3, feeds 41-7/41-8.

## Scope

Thin binding over `document-lifecycle`. `consumes: [BacklogOrdering (41-3), team capacity, prior SprintPlan
carry-over]` / `produces: SprintPlan`. Produce cell `(scrum_master, plan-sprint)` (41-1).

## Produced document

`SprintPlan` (41-1): committed set ≤ stated capacity; every committed item has an owner-role + estimate;
carry-over flagged. `tenantId`/`repository`/sprint lineage.

## Events

`SPRINT.PLANNING.STARTED` → `.PLANNED` → `.ACCEPTED` / `.CLOSED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; the accepted plan seeds Task View assignments per committed item's
owner-role. Over-commit beyond capacity is a validator rejection, not an accept-time surprise.

## Autonomy behavior

- **70–84:** agent proposes; scrum master/PM accepts the commitment.
- **85–100:** agent plans and self-accepts within capacity; commitment beyond a configured capacity band
  always escalates.

## Acceptance Criteria

1. Thin lifecycle binding; `SprintPlan` validated (capacity bound, owner+estimate per item, carry-over).
2. Consumes accepted `BacklogOrdering`; hard-fails loud if none exists.
3. Committed items produce role-scoped Task View entries via 39-20.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** 41-1 (`scrum_master` role, `SprintPlan` type), 41-3, Epic 39 (lifecycle, store, routing).
- **Related:** 41-7, 41-8.

## Estimated Effort

4–5 days
