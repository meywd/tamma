# Story 41-12: Dependency & Upgrade Planning Workflow

Status: drafted

## User Story

As an **architect** (with a **security** lens), I want a workflow that turns dependency/upgrade pressure
(from the 41-20 audit or a schedule) into a typed migration `Plan` on the lifecycle, so that upgrades are
sequenced, risk-assessed, and accepted before work starts — instead of a risky big-bang bump.

## Priority

P3 / Wave 3 — recurring maintenance; consumes 41-20 findings.

## Scope

Triggered by 41-20 findings or scheduled → thin binding over `document-lifecycle`. `consumes: [dependency
Findings (41-20), manifest, breaking-change advisories]` / `produces: Plan`. Produce cell
`(architect, plan-migration-strategy)` with a `(security, audit-dependencies)` review lens.

## Produced document

`Plan` (39-4): per-upgrade task with file map, ordering (dependencies resolvable), testing stated per
task, rollback note. `repository` lineage. Reviewed via panel (architect + security + relevant).

## Events

`DEP_UPGRADE.PLAN.STARTED` → `.DRAFTED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; an accepted plan hands off to the coding step (Epic 40) task-by-task. A
major-version upgrade of a load-bearing dependency can be an always-escalate class.

## Autonomy behavior

- **70–84:** agent drafts; architect accepts before work.
- **85–100:** agent drafts and self-accepts minor/patch upgrade plans; majors always escalate.

## Acceptance Criteria

1. Thin lifecycle binding; `Plan` validated (dependency ordering, per-task testing).
2. Consumes 41-20 dependency findings when present.
3. Accepted plan consumable by the coding step via 39-11.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (`Plan`, lifecycle, review-panel, store); Epic 40 for execution.
- **Related:** consumes 41-20.

## Estimated Effort

3–4 days
