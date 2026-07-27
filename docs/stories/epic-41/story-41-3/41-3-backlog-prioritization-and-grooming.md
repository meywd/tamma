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

The cell exists and nothing dispatches it — no 41-1a work here. What IS in scope is a **template
rewrite**: the shipped `Prompts/product_owner/prioritize-backlog.md` ranks ONE item and emits a
`TriageDecision`-shaped payload (P0–P3, `ownerRole`), not a total order over a set. It is rewritten to the
`BacklogOrdering` contract (39-15 D7 precedent). Evidence gathering is caller-supplied item set + bounded
per-item store reads (the store has no repository-wide query), degrading gracefully to issue text when the
upstream producers (41-11/41-16/41-17 — not yet built) have written nothing.

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
   Includes the `prioritize-backlog` template rewrite (see Scope).
2. Consumes upstream `TriageDecision`/`Findings` as ranking evidence via bounded per-item reads over the
   caller-supplied item set; absent evidence degrades to issue text and never hard-fails (41-11/41-16/41-17
   do not exist yet).
3. Consumable by 41-6 via the 39-11 store, under the synthetic backlog anchor
   (`BacklogOrdering` is not issue-scoped; `DocumentInstance.IssueId` is the only read key).
4. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched child); 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** **41-1b** (`BacklogOrdering` type), Epic 39 (lifecycle, store, accept).
- **Unblocks:** 41-6.

## Estimated Effort

3–4 days
