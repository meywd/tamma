# Story 41-10: System Design Document Workflow

Status: drafted

## User Story

As an **architect** (or eligible role-holder), I want a workflow that produces a typed `Design` document
for a larger feature — covering API contract, data model, and integration points with weighed
alternatives — on the standard lifecycle, so that non-trivial designs are proposed, reviewed, and accepted
before implementation planning, instead of being improvised in the plan step.

## Priority

P2 / Wave 3 — the depth counterpart to `design-proposal` for multi-surface features.

## Scope

Thin binding over `document-lifecycle`. `consumes: [issue, Findings, AcceptanceCriteria?, context-scan]` /
`produces: Design`. Produce cell `(architect, plan-system-design)`, drawing on the
`design-api-contract` / `design-data-model` / `design-integration` cells as sub-lenses folded into the one
`Design` document (rather than three separate workflows).

## Produced document

`Design` (39-4): ≥1 alternative with trade-offs; recommendation references an alternative; the API/data
/integration facets are sections of the one document. `issueId` lineage. Reviewed via panel (architect +
senior-dev + security + relevant lenses).

## Events

`SYSTEM_DESIGN.STARTED` → `.DRAFTED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; a design touching a public contract or cross-service boundary can be an
always-escalate class. Accepted `Design` is consumed by `plan-generation` and can seed 41-9 ADRs.

## Autonomy behavior

- **70–84:** agent drafts; architect accepts.
- **85–100:** agent drafts and self-accepts; contract/boundary-affecting designs always escalate per policy.

## Acceptance Criteria

1. Thin lifecycle binding; `Design` validated (alternatives + trade-offs + recommendation).
2. API/data/integration facets present as sections; no separate bespoke workflows.
3. Accepted `Design` consumable by `plan-generation` and 41-9 via 39-11.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (`Design`, lifecycle, review-panel, store).
- **Related:** feeds `plan-generation`, 41-9.

## Estimated Effort

4–5 days
