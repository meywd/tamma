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

## Acceptance Criteria

1. Scheduled, tenant-scoped, idempotent per window; each lens fail-closed.
2. Findings cite concrete metric evidence; empty ⇒ valid empty report.
3. `[ResumeBehavior(LatestStateReEntry)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (`Findings`, lifecycle, store), scheduler pattern, analytics/health signals
  (28-10 rollup, 4-7 query API).

## Estimated Effort

4–5 days
