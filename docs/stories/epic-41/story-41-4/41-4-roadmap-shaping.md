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

1. Thin lifecycle binding; prose reviewed by a `Review`.
2. Consumes `BacklogOrdering`/`Findings` as inputs.
3. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (prose handling, lifecycle, review, store); 41-3.

## Estimated Effort

3 days
