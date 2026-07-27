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
carry-over flagged. `tenantId`/`repository`/sprint lineage — a `SprintPlan` is not issue-scoped, so it
keys on its own lineage anchor `sprint:{repository}:{sprintKey}`. Reviewed by a **`product_owner`**
reviewer: `scrum_master` joins neither review panel ("they produce and accept, they do not critique
documents" — 41-1a D2) and the review-action selector throws for any unlisted role, so "the scrum
master's plan is reviewed by the scrum master" is not an option.

## Events

`SPRINT.PLANNING.STARTED` → `.PLANNED` → `.ACCEPTED` / `.CLOSED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy. Over-commit beyond capacity is a validator rejection, not an accept-time
surprise. The accepted plan carries an owner-role per committed item so that Task View assignment becomes
a pure consumer of the document once 39-19/39-20 land — seeding those assignments is **not** reachable
today (see AC3).

## Autonomy behavior

- **70–84:** agent proposes; scrum master/PM accepts the commitment.
- **85–100:** agent plans and self-accepts within capacity; commitment beyond a configured capacity band
  always escalates.

## Acceptance Criteria

1. Thin lifecycle binding; `SprintPlan` validated (capacity bound, owner+estimate per item, carry-over).
2. Consumes accepted `BacklogOrdering` (via 41-3's synthetic anchor); a missing ordering is a **typed loud
   exit** — a `SPRINT.PLANNING.FAILED` emission plus `status="failed"` with a named detail (the read seam
   is fail-closed and never throws, and rule 1 forbids a `Finish` terminal, so "hard-fail" is a routed
   outcome, not an exception).
3. **Not claimable until 39-19/39-20 land** (the audience resolver is the fail-closed
   `InitiatorOnlyTaskAudienceResolver` stub and there is no Task View). This story delivers the
   precondition only: the accepted `SprintPlan` carries an owner-role per committed item and acceptance
   publishes the standard `AcceptanceRequest`; role-scoped Task View entries become a consumer of the
   document when 39-19/39-20 land, with no edit to this binding.
4. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched child); 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** **41-1a** (`scrum_master` role + `plan-sprint` cell) **and 41-1b** (`SprintPlan`
  type), 41-3, Epic 39 (lifecycle, store, routing).
- **Related:** 41-7, 41-8.

## Estimated Effort

4–5 days
