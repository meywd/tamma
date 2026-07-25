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

Scheduled trigger (the `HourlyAnalyticsRollupScheduler` cron pattern, daily per configured team-window) →
thin binding over `document-lifecycle`. `consumes: [DCB events window, open Decompositions/Plans/PRs,
blocker events]` / `produces: Findings`. Produce cell `(scrum_master, synthesize-standup)` (41-1).

## Produced document

`Findings`: each item cites its source event(s) as evidence; ranked by risk; blocked/at-risk items flagged
with the owning role. `issueId`/`repository` lineage on every finding.

## Events

`STANDUP.SYNTHESIS.STARTED` → `.DIGEST` alongside `DOCUMENT.*`, tagged `repository`/`tenantId`/window.

## Orchestrator / user interaction

The accepted digest is delivered to the team via the orchestrator (chat post + Task View items for each
flagged blocker, routed to the owning role). Low-value/empty windows produce an empty-but-valid `Findings`
(no false noise).

## Autonomy behavior

- **70–84:** agent drafts; scrum master reviews before the digest is broadcast.
- **85–100:** agent synthesizes and self-accepts; each flagged blocker still routes to its owning role's
  Task View as an assigned follow-up.

> **Epic 42 caveat — the agent path cannot *broadcast* yet.** Publishing the digest to a chat/tracker
> needs an authenticated HTTP / external-API tool (**42-9**); the six registered `IToolExecutor`s
> (`Tamma.Api/Program.cs:753-764`) are all coding-oriented. Synthesis is agent-reachable; delivery is
> **human-assigned** (rule 4) until 42-9 lands.

## Acceptance Criteria

1. Scheduled, tenant-scoped, idempotent per window (re-running the same window is a no-op re-read).
2. Every finding cites concrete DCB evidence; confidence/relevance ∈ [0,1]; empty window ⇒ valid empty digest.
3. `[ResumeBehavior(LatestStateReEntry)]`; 39-10 structural test green without allowlist.
4. Blocker follow-ups land in the correct role's Task View via the 39-20 audience resolver.

## Dependencies

- **Blocking:** **41-1a** (`scrum_master` role + `synthesize-standup` cell), Epic 39 (`Findings`,
  lifecycle, store, task routing, 4-7 query API), and **the tenant-aware scheduled-trigger seam — unowned; no story writes it** (*corrected: "scheduler pattern" named no artifact;* `HourlyAnalyticsRollupScheduler` *is hardcoded to one workflow (`:198-199`), offers one `FireAtMinute` int rather than a window/cron shape (`:34`), threads no `tenantId` into the dispatch (`:202-203`), keeps its last-fired window in a per-process field (`:83`), and its advisory-lock key has no tenant component (`:241`) — one tenant's leader would suppress every other tenant's fire*).
- **Related:** feeds 41-8 retro input.

## Estimated Effort

4–5 days
