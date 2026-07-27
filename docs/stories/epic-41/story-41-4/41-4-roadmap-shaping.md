# Story 41-4: Roadmap Shaping Workflow

Status: drafted

## User Story

As a **product owner** (or eligible role-holder), I want a workflow that shapes a themed, time-horizon
roadmap from the backlog and strategic inputs, as an audience-tagged prose document on the lifecycle, so
that direction is captured, reviewed, and accepted with lineage instead of living in a slide deck.

## Priority

P3 / Wave 3 — strategic altitude; lower cadence than sprint/backlog work.

## Scope

Thin binding over `document-lifecycle`. `consumes: [BacklogOrdering (41-3), Findings, stakeholder inputs]`
/ `produces: prose (roadmap, audience=stakeholder)`. Produce cell `(product_owner, plan-roadmap)`.

The cell exists and nothing dispatches it — no 41-1a work here. What IS in scope is a **template
rewrite**, the largest in the batch: the shipped `Prompts/product_owner/plan-roadmap.md` emits a
file-level implementation `Plan` (JSON tasks with create/modify file actions), not a themes × horizons
prose roadmap. It is rewritten from a JSON task emitter to a markdown prose author inside 41-1c's
`{kind, audience, title, body}` envelope. The `BacklogOrdering` is read through 41-3's synthetic anchor
(`BacklogBindingHelper.BuildAnchor`); "stakeholder inputs" have no store representation and are a
caller-supplied input string. The roadmap itself is not issue-scoped and keys on its own lineage anchor
(`roadmap:{repository}:{horizonScope}`). The review stage pins a `product_owner` reviewer for
`kind=roadmap` (the 41-1c default `tech_writer` reviewer would throw in the selector until 41-1a lands —
41-1a stays a soft upgrade, not a gate).

## Produced document

Audience-tagged prose roadmap (themes × horizons with rationale). *Prose stays prose*; review stage is a
`Review` over the text. `tenantId` lineage.

## Events

`ROADMAP.STARTED`/`.DRAFTED`/`.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; a roadmap is typically a human-accepted artifact even at high autonomy
(strategic) — expressed as an always-escalate policy class, not hardcoded.

## Autonomy behavior

- **70–94:** agent drafts; PO accepts.
- **95–100:** agent drafts; acceptance still routed to a human by default policy for strategic scope.

## Acceptance Criteria

1. Thin lifecycle binding; prose reviewed by a `Review` (reviewer pinned to `product_owner` for
   `kind=roadmap`). Includes the `plan-roadmap` template rewrite (see Scope).
2. Consumes `BacklogOrdering` (via 41-3's synthetic anchor) / `Findings` as inputs; stakeholder inputs are
   caller-supplied.
3. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched child); 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** **41-1c** (the `prose` type + `Audience` field — this workflow's output is prose and
  the mechanism does not exist in code; *corrected: this line named "Epic 39 (prose handling)", which
  39-1:58 records as out of Epic 39's scope*), Epic 39 (lifecycle, review, store); 41-3.

## Estimated Effort

3 days
