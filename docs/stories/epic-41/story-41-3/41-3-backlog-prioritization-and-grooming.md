# Story 41-3: Backlog Prioritization & Grooming Workflow

Status: drafted

## User Story

As a **product owner** (or eligible role-holder), I want a workflow that ranks a set of backlog items into
a typed `BacklogOrdering` on the lifecycle — with value/effort rationale per item — so that prioritisation
is explicit, reviewed, accepted, and consumable by sprint planning, instead of an ad-hoc reorder.

## Priority

P2 / Wave 3 — feeds 41-6 sprint planning and 41-4 roadmap.

## Scope

Thin binding over `document-lifecycle`. `consumes: [backlog items (issues), TriageDecisions from
41-11/41-16/41-17, Findings]` / `produces: BacklogOrdering`. Produce cell
`(product_owner, prioritize-backlog)`.

## Produced document

`BacklogOrdering` (41-1): total order over the referenced item set; every item has a rationale +
value/effort estimate; no ties. `tenantId`/`repository` lineage.

## Events

`BACKLOG.GROOMING.STARTED` → `.ORDERED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; accepted ordering is the input 41-6 reads. Large reprioritisations
affecting committed work can be an always-escalate class.

## Autonomy behavior

- **70–84:** agent proposes an ordering; PO accepts.
- **85–100:** agent orders and self-accepts within policy; reordering above a churn threshold escalates.

## Acceptance Criteria

1. Thin lifecycle binding; `BacklogOrdering` validated (total order, rationale per item, no ties).
2. Consumes upstream `TriageDecision`/`Findings` as ranking evidence.
3. Consumable by 41-6 via the 39-11 store.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** 41-1 (`BacklogOrdering` type), Epic 39 (lifecycle, store, accept).
- **Unblocks:** 41-6.

## Estimated Effort

3–4 days
