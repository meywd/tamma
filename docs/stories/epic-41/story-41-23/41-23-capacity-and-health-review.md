# Story 41-23: Capacity & Health Review Workflow

Status: drafted

## User Story

As a **devops** engineer (or eligible role-holder), I want a scheduled workflow that reviews system health
signals and capacity trends and produces a typed `Findings` report, so that saturation and degradation are
flagged proactively and routed, instead of surfacing only as an incident.

## Priority

P2 / Wave 2 — recurring, ops-hygiene; pairs with the reactive 41-22.

## Scope

Scheduled sweep → thin binding over `document-lifecycle`. `consumes: [health/metric signals, deployment +
analytics events]` / `produces: Findings`. Produce cells `(devops, monitor-health)` and
`(devops, assess-capacity)` as lenses aggregating into one report.

## Produced document

`Findings`: each finding cites a metric/trend as evidence, with severity + recommended action; ranked;
projected-breach items flagged.

## Events

`HEALTH_REVIEW.STARTED`/`.REPORT` alongside `DOCUMENT.*`, tagged `repository`/`tenantId`/window.

## Orchestrator / user interaction

Accepted report routes per autonomy; a projected-capacity breach or degraded-health finding is assigned to
the devops role's Task View or self-actioned at high autonomy (can seed a scaling/infra task).

## Autonomy behavior

- **70–84:** agent drafts; devops reviews before actioning.
- **85–100:** agent drafts and self-accepts; routine scaling recommendations auto-assigned; a breach above
  a configured threshold always escalates.

> **Epic 42 caveat — no registered tool can read a health or metric signal.** All six registered
> `IToolExecutor`s (`Tamma.Api/Program.cs:753-764`) are coding-oriented; reading metrics needs
> **42-9** (authenticated HTTP) and acting on capacity needs **42-7** (cloud/VPS). Until then this
> workflow is **human-assigned** (rule 4) for anything beyond the DCB/analytics reads it already has.

## Acceptance Criteria

1. Scheduled, tenant-scoped, idempotent per window; each lens fail-closed.
2. Findings cite concrete metric evidence; empty ⇒ valid empty report.
3. `[ResumeBehavior(LatestStateReEntry)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (`Findings`, lifecycle, store), analytics/health signals (28-10 rollup, 4-7
  query API), and **the tenant-aware scheduled-trigger seam — unowned; no story writes it** (*corrected: "scheduler pattern" named no artifact;* `HourlyAnalyticsRollupScheduler` *is hardcoded to one workflow (`:198-199`), offers one `FireAtMinute` int rather than a window/cron shape (`:34`), threads no `tenantId` into the dispatch (`:202-203`), keeps its last-fired window in a per-process field (`:83`), and its advisory-lock key has no tenant component (`:241`) — one tenant's leader would suppress every other tenant's fire*).

## Estimated Effort

4–5 days
