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
`(architect, plan-migration-strategy)` with a `(security, audit-dependencies)` review lens. Both cells
exist today (`AgentAction.cs`, with shipped templates) and are unbound — this story binds the first.

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

1. Thin lifecycle binding on `(architect, plan-migration-strategy)`, adding one `ContractBindingTests`
   `Bindings` entry with authority `PlanDocumentType.Validate`.
2. `Plan` validation is exercised by one fixture per rule: no tasks ⇒ `EMPTY_PLAN`; a task with no file
   map ⇒ `TASK_MISSING_FILE_MAP`; a task with no testing ⇒ `TASK_MISSING_TESTING`; an upgrade that depends
   on an unlisted task ⇒ `DANGLING_DEPENDS_ON`; a mutually-blocking pair ⇒ `CYCLIC_DEPENDS_ON`; an
   unorderable set ⇒ `NO_TOPOLOGICAL_ORDER` (`Plan.cs:50-71`). Upgrade ordering is therefore *checked*,
   not asserted.
3. The run records the `documentId` of the 41-20 `Findings` it consumed (or `null` when triggered by
   schedule with none present), and fails loud if a referenced id is unreadable — never silently plans
   against no input.
4. An accepted `Plan` is retrievable by `repository` through 39-11 and is read by a coding-step dispatch in
   an integration test.
5. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate
   suspends inside the dispatched `document-lifecycle` child); 39-10 structural test green without an
   allowlist entry. A new
   `WorkflowDocumentInterface` row is declared and `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned`
   is bumped in the same change.

> Whether the upgrade *sequence is the right one*, and whether risk was assessed *well*, are not
> acceptance criteria — no deterministic check exists. That is the architect + security panel's job.

## Dependencies

- **Blocking:** Epic 39 (`Plan`, lifecycle, review-panel, store).
- **Blocking for the execution hand-off only (AC4's downstream, not this workflow):** **Epic 40**.
  *Corrected: this previously read "Epic 40 for execution", which reads as a durability nicety. Epic 40
  ships the missing **execution substrate** — `.github/workflows/tamma-agent.yml` does not exist in this
  repo, so the coding step's dispatch fails loud with `WorkflowNotFound`
  (`AgentDispatchMediationService.cs:109`) today. Producing and accepting the `Plan` has no Epic 40
  dependency; only working the accepted plan does.*
- **Related:** consumes 41-20.

## Estimated Effort

3–4 days
