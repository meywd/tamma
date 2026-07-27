# Story 41-5: Stakeholder & Status Reporting Workflow

Status: drafted

## User Story

As a **product owner / project manager** (or eligible role-holder), I want a scheduled workflow that
synthesizes progress against commitments from the DCB stream into an audience-tagged status update, so that
stakeholders get an accurate, evidence-backed report on a cadence without manual assembly.

## Priority

P2 / Wave 3 — recurring, cross-role; reuses the 41-7 event-read pattern at a stakeholder altitude.

## Scope

Scheduled trigger → thin binding over `document-lifecycle`. `consumes: [SprintPlan (41-6, optional and
fail-closed — the report degrades to DCB evidence only when no accepted SprintPlan exists), DCB events for
the period, blocker/escalation events]` / `produces: prose (status-update, audience=stakeholder)`. Produce
cell: **`(project_manager, report-status)` via 41-1a — the primary and only produce cell.**
`(product_owner, summarize-stakeholder)` is NOT usable: it is already dispatched live by
`ContextGatheringWorkflow` as a lenient free-text summarizer and classified `IntentionallyUnbound`
(`Bindings` and `IntentionallyUnbound` are mutually exclusive — binding it fails the build), so it stays
untouched. The status report is not issue-scoped; it keys on the deterministic lineage anchor
`status:{repository}:{periodKey}`.

## Produced document

Audience-tagged prose status update (accomplished / in-flight / at-risk / next), each claim traceable to
DCB evidence. `tenantId`/period lineage. Review stage is a `Review` over the text.

## Events

`STATUS_REPORT.STARTED`/`.DRAFTED`/`.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accepted report delivered via the orchestrator to the stakeholder audience; sensitive/external reporting
can be an always-escalate class (human sign-off before send).

## Autonomy behavior

- **70–84:** agent drafts; PO/PM accepts before send.
- **85–100:** agent drafts and self-accepts internal reports; external stakeholder reports per policy.

> **Epic 42 caveat — the agent path cannot *publish* yet.** Posting the report to Slack/Jira/email
> needs an authenticated HTTP / external-API tool (**42-9**). Only six `IToolExecutor`s are registered
> today (`Tamma.Api/Program.cs:753-764`), all coding-oriented. Until 42-9 lands, drafting is
> agent-reachable but delivery is **human-assigned** (rule 4) — not a day-one agent path.

## Acceptance Criteria

1. Scheduled, tenant-scoped, idempotent per period; every claim cites DCB evidence (requires a new
   tenant-scoped, time-windowed DCB read activity over `IEventRepository.QueryEventsAsync` — no such
   activity exists today; it is in scope here and shared with 41-7).
2. Thin lifecycle binding; prose reviewed by a `Review`.
3. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched child; and "scheduled" is a dispatcher concern, not a resume mode); 39-10
   structural test green without allowlist.

## Dependencies

- **Blocking:** **41-1a** (the `project_manager` role + `(project_manager, report-status)` cell — the
  produce cell does not exist today; see Scope), **41-1c** (the `prose` type + `Audience` field;
  *corrected: was "Epic 39 (prose handling)" — out of Epic 39's scope per 39-1:58*), Epic 39 (lifecycle,
  review, store, 4-7 query API).
- **Related:** consumes 41-6 SprintPlan (optional, fail-closed — not a hard dependency). Per the
  2026-07-25 scheduling decision, reporting is user-initiated: this story is NOT blocked on the 41-30
  scheduled-trigger seam (a cron cadence is a later opt-in through 41-30).

## Estimated Effort

3–4 days
