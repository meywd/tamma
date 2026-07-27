# Story 41-11: Tech-Debt & Technical-Risk Triage Workflow

Status: drafted

## User Story

As an **architect** (or eligible role-holder), I want a scheduled workflow that scans the codebase + DCB
history for accumulating technical debt and standing risks, triages each into a typed `TriageDecision`,
and (for the top items) a `Findings` risk assessment, so that debt is surfaced and prioritised on a cadence
instead of only when it causes an incident.

## Priority

P2 / Wave 2 — recurring, compounding; keeps the architecture honest between features.

## Scope

Scheduled sweep → thin binding over `document-lifecycle`. `consumes: [codebase scan (context-scan),
DCB events, dependency/build signals]` / `produces: TriageDecision` per debt/risk item, plus a ranked
`Findings` for the top-N. Produce cells `(architect, triage-tech-debt)` (41-1) and
`(architect, assess-technical-risk)`.

## Produced documents

`TriageDecision` (severity/category/effort/urgency, reasoning required) and `Findings` (evidence-cited,
ranked technical-risk assessment).

## Events

`TECH_DEBT.SWEEP.STARTED`/`.ITEM`/`.COMPLETED` alongside `DOCUMENT.*`, tagged `repository`/`tenantId`.

## Orchestrator / user interaction

Accepted items route to the backlog via the orchestrator (can seed 41-3 backlog ordering / 41-6 sprint
planning); high-risk items assigned to the architect/senior-dev role's Task View.

## Autonomy behavior

- **70–84:** agent surfaces candidates; architect confirms which become backlog items.
- **85–100:** agent triages and self-accepts; backlog seeding automatic; a risk above a configured
  threshold always escalates.

## Acceptance Criteria

1. Scheduled, tenant-scoped, idempotent; fail-closed per item.
2. Closed-enum classification with required reasoning; risk `Findings` cite concrete evidence.
3. Output consumable by 41-3/41-6 as backlog candidates.
4. `[ResumeBehavior(LatestStateReEntry)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** **41-1a** (`triage-tech-debt` cell — absent from `AgentAction.cs` today), Epic 39
  (`TriageDecision`, `Findings`, lifecycle, store), `context-gathering`, and **the tenant-aware scheduled-trigger seam — now owned by 41-30 (cadence AC only; the producing half is buildable before it)** (*corrected: "scheduler pattern" named no artifact;* `HourlyAnalyticsRollupScheduler` *is hardcoded to one workflow (`:198-199`), offers one `FireAtMinute` int rather than a window/cron shape (`:34`), threads no `tenantId` into the dispatch (`:202-203`), keeps its last-fired window in a per-process field (`:83`), and its advisory-lock key has no tenant component (`:241`) — one tenant's leader would suppress every other tenant's fire*).

## Estimated Effort

4–5 days
