# Story 41-7: Standup Synthesis Workflow

Status: drafted

## User Story

As a **scrum master** (or eligible role-holder), I want a scheduled workflow that reads the DCB event
stream for a team/repo over the last day and synthesizes a **standup digest** (what moved, what's blocked,
what's at risk) as a typed `Findings` document, so that the daily status picture is assembled from the
audit trail automatically instead of collected by hand.

## Priority

P1 / Wave 2 — recurring, event-sourced, compounding. Replaces a standing daily chore and showcases the
"read the stream on a cron" pattern for 41-11/41-16/41-20/41-23.

## Scope

User-initiated run over a configured team-window (per the 2026-07-25 scheduling decision, ceremonies are
user-initiated — a cron cadence is a later opt-in through 41-30; `HourlyAnalyticsRollupScheduler` is not a
reusable pattern) → thin binding over `document-lifecycle`. `consumes: [DCB events window, open
Decompositions/Plans/PRs, blocker events]` / `produces: Findings`. Produce cell
`(scrum_master, synthesize-standup)` (41-1).

## Produced document

`Findings`: each item cites its source event(s) as evidence; ranked by risk; blocked/at-risk items flagged
with the owning role. `issueId`/`repository` lineage on every finding.

## Events

`STANDUP.SYNTHESIS.STARTED` → `.DIGEST` (or `.SKIPPED` for an empty window) alongside `DOCUMENT.*`,
tagged `repository`/`tenantId`/window.

## Orchestrator / user interaction

The accepted digest is delivered to the team via the orchestrator (chat post + Task View items for each
flagged blocker, routed to the owning role). An empty window **short-circuits before dispatch**: it emits
`STANDUP.SYNTHESIS.SKIPPED` and produces no document (no false noise — `FindingsDocumentType`
deliberately rejects an empty findings list with `EMPTY_FINDINGS`, so "an empty-but-valid `Findings`" is
not a thing the type permits).

## Autonomy behavior

- **70–84:** agent drafts; scrum master reviews before the digest is broadcast.
- **85–100:** agent synthesizes and self-accepts; each flagged blocker still routes to its owning role's
  Task View as an assigned follow-up.

> **Epic 42 caveat — the agent path cannot *broadcast* yet.** Publishing the digest to a chat/tracker
> needs an authenticated HTTP / external-API tool (**42-9**); the six registered `IToolExecutor`s
> (`Tamma.Api/Program.cs:753-764`) are all coding-oriented. Synthesis is agent-reachable; delivery is
> **human-assigned** (rule 4) until 42-9 lands.

## Acceptance Criteria

1. Tenant-scoped, idempotent per window (re-running the same window is a no-op re-read); user-initiated
   per the 2026-07-25 scheduling decision.
2. Every finding cites concrete DCB evidence; confidence/relevance ∈ [0,1]. An empty window produces **no
   document and a `STANDUP.SYNTHESIS.SKIPPED` audit row** — the run short-circuits before dispatch with
   `status = "skipped"`; never an empty `Findings` (the type rejects one with `EMPTY_FINDINGS`) and never
   a false digest.
3. `[ResumeBehavior(LatestStateReEntry)]`; 39-10 structural test green without allowlist.
4. Each flagged blocker is emitted as a `STANDUP.BLOCKER_FLAGGED` row carrying the owning role, and the
   accepted digest publishes an `AcceptanceRequest` on the orchestrator channel; **role-scoped Task View
   delivery is unreachable until 39-19/39-20 land** (the audience resolver is the fail-closed
   `InitiatorOnlyTaskAudienceResolver` stub).

## Dependencies

- **Blocking:** **41-1a** (`scrum_master` role + `synthesize-standup` cell), Epic 39 (`Findings`,
  lifecycle, store, task routing, 4-7 query API).
- **Related:** feeds 41-8 retro input. Per the 2026-07-25 scheduling decision, standup synthesis is
  **user-initiated** — this story is NOT blocked on the tenant-aware scheduled-trigger seam (now owned by
  **41-30**); a cron cadence is a later opt-in through 41-30. (*The old blocking line's finding stands:*
  `HourlyAnalyticsRollupScheduler` *is hardcoded to one workflow, threads no `tenantId`, and its
  advisory-lock key has no tenant component — which is exactly why 41-30 exists.*)

## Estimated Effort

4–5 days
