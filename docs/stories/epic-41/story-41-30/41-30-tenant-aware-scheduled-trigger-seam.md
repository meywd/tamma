# Story 41-30: The Tenant-Aware Scheduled-Trigger Seam

Status: drafted

## User Story

As the **platform**, and on behalf of every recurring audit workflow in this epic, I want a
**tenant-aware, durable, at-most-once-per-window scheduled-trigger seam** that can fire *any* workflow
definition on a cron cadence with a `tenantId` and an opaque `windowKey` threaded into its inputs, so
that recurring audits run **whether or not anyone remembered** — once per tenant per window, across a
multi-pod fleet, surviving process restarts, and without one tenant's leader suppressing another
tenant's fire.

## Priority

**P0 / Wave 0.** This is the **fourth Wave-0 enabler** and the only one that had no story until this one was filed
(epic README, *Sequencing → Wave 0*; `docs/sprint-status.yaml`, epic-41 block). It hard-blocks the AC1 of **five**
audit stories and Wave 2 in its entirety.

## Shape: a hosted service + control-plane storage + an admin surface — **not a workflow**

This story is deliberately **not** a new Elsa workflow, and not an amendment to an existing one. The
reasoning, because the epic's default answer is "make it a workflow":

- **A workflow cannot start itself on a cadence.** Something outside the engine's request path has to
  wake up on a clock. Every existing cadence in the tree is a `BackgroundService`
  (`HourlyAnalyticsRollupScheduler`, `SecretAutoRotationScheduler`, `RetireSweepHostedService`,
  `RevealTokenSweeper`, `ChannelOutboxSweeper`, `AlertRuleEvaluator`, …) — there is no counter-example.
- **Elsa's own `Cron` trigger activity is not a substitute** even though it is already available (see
  Correction 1). It arms **one** trigger per workflow *definition* and carries no tenant dimension, so
  it cannot fan a single cadence out over N tenants; and `Elsa.Scheduling`'s shipped scheduler is
  `LocalScheduler` — in-process, so an N-pod deploy arms N copies.
- **The seam produces no document and rides no lifecycle**, so rule 1's thin-binding recipe does not
  apply to it. It is *infrastructure under* the thin bindings, not another one of them. Its consumers
  (`security-audit`, `tech-debt-triage`, `pr-triage-sweep`, …) are the document producers; they already
  take `tenantId` + `windowKey` as plain inputs and are written trigger-agnostic on purpose
  (41-20's implementation plan, D8).

So: **one hosted service, two control-plane tables, one admin API, one Epic 43 catalog entry.** Zero
new workflows, zero new `(role, action)` cells, zero new document types.

## Scope

1. **`scheduled_triggers`** (control plane) — the schedule definitions. One row per
   `(tenant_id, definition_id, name)`: cron expression, enabled flag, an opaque `input_json` merged
   into the dispatch, `next_due_at`, `last_window_key`, `last_fired_at`, plus the usual audit columns.
   A `tenant_id IS NULL` row is a **platform default template**; materialising it per tenant is D6.
2. **`scheduled_trigger_fires`** (control plane) — the durable at-most-once ledger. `UNIQUE
   (trigger_id, window_key)`. A row is inserted **before** the dispatch; a unique-violation *is* the
   dedupe answer. This is the property `_lastFired`-in-process-memory does not have and cannot have.
3. **`TenantScheduledTriggerService : BackgroundService`** in `Tamma.ElsaServer` — polls, computes due
   windows per tenant, takes a **tenant-scoped** advisory lock, claims the fire in the ledger, and
   dispatches through the in-process `IWorkflowDispatcher` with `tenantId` + `windowKey` + the row's
   `input_json` as workflow inputs.
4. **Cron evaluation with no new dependency** — `Cronos` is already in the graph (Correction 1).
5. **Catch-up policy, bounded** — a tenant whose engine was down for a day does not get 24 audits. One
   run for the most recent missed window, and a `SCHEDULE.WINDOW.SKIPPED` event per window dropped, so
   the gap is *auditable* rather than invisible.
6. **Admin API + RBAC**, answered separately per operating mode (D7), per CLAUDE.md's universal rule.
7. **`SCHEDULE.*` DCB events** so a fire, a skip and a suppression are all in the audit trail.

## Explicitly out of scope

- **Any consumer workflow.** 41-11 / 41-16 / 41-17 / 41-20 / 41-23 each ship their own binding; this
  story wires **none** of them. It ships with its consumers *registerable*, not registered.
- **Migrating `HourlyAnalyticsRollupScheduler` onto the seam.** It is platform-global by design (the
  rollup aggregates *across* tenants) and its target is genuinely fixed. Converting it would be a
  behaviour change to a landed, working component for no consumer benefit. Recorded as a follow-up,
  not done here.
- **Migrating `SecretAutoRotationScheduler`.** Its unit of scheduling is a *secret row's*
  `NextRotationDueAt`, not a cron window — a different shape that this seam does not model.
- **A UI.** The API is the surface; a screen is a 43-7-adjacent follow-up.

## Events

`SCHEDULE.FIRE.DISPATCHED` (tags `tenantId`, `definitionId`, `windowKey`, `triggerId`),
`SCHEDULE.FIRE.SUPPRESSED` (another pod won the window — INFO, not an error),
`SCHEDULE.WINDOW.SKIPPED` (LOUD — catch-up dropped a window, with the count),
`SCHEDULE.FIRE.FAILED` (LOUD — dispatch threw or timed out. *[Amended 2026-07-29]* the ledger row is
**stamped `failed` and the window is burnt** — the NEXT window is the recovery path; there is no
same-window retry. The original "row is released so the next tick retries" wording contradicted the
at-most-once contract the implementation ships),
`SCHEDULE.TRIGGER.CHANGED` (an admin created/updated/disabled a schedule).

## Autonomy behavior

None — this component makes no quality decision and produces no document, so the autonomy dial does not
apply to *it*. The dial governs the workflows it dispatches, unchanged. What Epic 43 *does* own here is
(a) the background actor itself as an `automation:*` catalog member and (b) the schedule mutations as
`effect:schedule.*` members (D8).

## Acceptance Criteria

1. **At most one dispatch per `(tenantId, definitionId, windowKey)` across the whole fleet, durably.**
   Proven with two pods racing the same window, and again across a process kill between the ledger
   claim and the dispatch.
2. **Tenant isolation.** Tenant A's fire never suppresses tenant B's for the same window — the exact
   defect at `HourlyAnalyticsRollupScheduler.cs:241`, pinned by a regression test that fails if a
   tenant component is ever dropped from the lock key.
3. **Target-agnostic.** The workflow definition id is **row data**, never a compile-time constant. A
   test asserts no `DefinitionId` constant of any consumer workflow appears in the service's source.
4. **`tenantId` and `windowKey` reach the dispatched workflow as inputs**, and a workflow dispatched
   twice with the same `windowKey` is the consumers' own idempotency contract (41-20 D3) — this story
   guarantees it is not *called* twice, and states plainly that it does not guarantee the target is
   idempotent.
5. **Cron shape, not a `FireAtMinute` int**; standard 5-field expressions evaluated in UTC; a malformed
   expression is rejected **at write time** by the admin API with a typed error, never at fire time.
6. **Bounded catch-up.** After a 24-hour outage an hourly schedule fires **once**, and emits
   `SCHEDULE.WINDOW.SKIPPED` naming the number of windows dropped.
7. **The two tables are excluded from the destructive startup DROP list** (`Program.cs:3243-3282`) — a
   deploy must not silently disable every tenant's audits. Pinned by a test that reads the DROP
   statement and asserts neither table name appears (D9).
8. **Registered in Epic 43's catalog**: one `BackgroundActor` / `automation:*` member so 43-8's
   bidirectional hosted-service sweep stays green, and `effect:schedule.create|update|delete` for the
   admin mutations.
9. **Disabled by default** (`Enabled = false`, the `SecretAutoRotationScheduler` precedent), so
   landing this story changes no running deployment's behaviour until an operator opts in.

## Dependencies

- **Blocking:** nothing. Every input exists today — `ControlPlaneDbContext` is already wired into
  `Tamma.ElsaServer` (`Program.cs:198-208`), `IWorkflowDispatcher` is in-process there, the active-tenant
  enumeration pattern is landed (`QueuedTaskRepository.ListPendingFromAnyTenantAsync`), the advisory-lock
  primitive is landed (`IRollupSchedulerLeaderLock` / `PostgresAdvisoryLeaderLock`), and the cron parser
  is already in the dependency graph (Correction 1). **This story can start immediately and in parallel
  with 41-1a/41-1b/41-1c.**
- **Blocks (AC1 of each):** **41-11** tech-debt sweep, **41-16** regression/flaky management, **41-17**
  PR-triage half, **41-20** scheduled security audit, **41-23** capacity & health review. Wave 2 cannot
  complete without it.
- **Does NOT block 41-5 or 41-7.** Per the product owner's 2026-07-25 decision, ceremonies are
  user-initiated; both need only the manual trigger that already exists. `docs/sprint-status.yaml`
  still lists them as scheduler-blocked — corrected in the same change as this story.
- **Interlocks with Epic 43:** 43-2 (`BackgroundActor` enum), 43-3 (group assignment — `platform-automation`
  for the actor, `platform-automation` for the `effect:schedule.*` trio), 43-8 (the hosted-service sweep
  that will go red if the actor is not registered). If 43-2 has not landed, ship the service and file
  the catalog member as a named follow-up in 43-3's expected-set test — do **not** invent a parallel
  vocabulary.
- **Related:** `RotationScheduleCalculator.RegisterCronEvaluator` (`Tamma.Api`) is a still-empty cron
  seam from Story 29-2. This story does **not** fill it (different assembly, different unit of
  scheduling), but it does prove out the parser choice for whoever does.

## Estimated Effort

**5–6 days.** (2 storage + migration, 1.5 service, 1 admin API + RBAC, 1.5 tests incl. the two-pod race
and the restart scenario.)

## Amendments — 2026-07-29 (adversarial-review fixes)

The following behaviour contracts changed (or were made explicit) relative to the text above:

1. **Manual run-now fires are now at-most-once (MAJOR-1, fixed).** The engine's manual drain no
   longer dispatches straight off the pending list. Each pending `manual:{timestamp}` row must first
   be won via a conditional CAS (`ScheduledTriggerRepository.TryClaimManualFireForDispatchAsync`:
   `UPDATE … SET "DispatchedAt" = @now WHERE "Id" = @id AND "Outcome" = 'claimed' AND "DispatchedAt"
   IS NULL`, arbitrated by Postgres row-count) — exactly one pod wins; concurrent pods skip. A crash
   or a failed outcome stamp after a won CAS **burns** the fire (the pending list filters
   `DispatchedAt IS NULL`), and a dispatch failure stamps `failed` — matching the cron path's
   burn-the-window semantics. A pending manual row can never be double-dispatched or re-dispatched in
   a loop. Proven by an 8-way real-Postgres CAS race test and an 8-pod service-level drain race test.
2. **Bounded catch-up now provably fires the LATEST missed window (MAJOR-2, fixed).** The old
   calculator collected due windows into an ascending list capped at 1000 and the service fired the
   list's last element — so a backlog over the cap (minutely cron + >16.7 h gap) fired a ~7 h-stale
   window and never accounted for the newest ones. `ScheduleWindowCalculator.ComputeDue` now yields
   the true latest due occurrence regardless of backlog size; the due count is bounded and, when the
   bound is hit, flagged (`skippedCountSaturated: true` in the `SCHEDULE.WINDOW.SKIPPED` event data,
   meaning `skippedCount` is "at least N"). AC6's "most recent missed window" now holds unconditionally.
3. **Per-dispatch timeout (MODERATE-3, fixed).** New option `ScheduledTriggers:DispatchTimeout`
   (default 30 s; non-positive disables). A dispatch that exceeds it — even one that ignores its
   cancellation token — is stamped `failed` (burn-the-window) and the fire loop continues, so one
   hung dispatch cannot stall the remaining tenants on the pod while holding the advisory-lock
   connection.
4. **Template materialisation is inside the tick's failure isolation (MODERATE-4, fixed).** The
   repository isolates each template (a poison template row is logged and skipped, the rest still
   materialise) and the service additionally wraps the whole materialisation call, so a wholesale
   failure can no longer abort every tick forever — existing concrete triggers keep firing.
5. **Run-now on a DISABLED trigger is a 409 (`trigger_disabled`), and the manual drain only
   dispatches enabled triggers' claims.** Contract decision resolving the previous ambiguity (the
   repository interface doc promised "(enabled) trigger rows" but neither the query nor the endpoint
   checked). A claim that was pending when its trigger got disabled waits, un-burnt, and drains after
   re-enablement.
6. **Same-millisecond duplicate run-now is a 409 (`duplicate_run_now`)**, mirroring Create's
   duplicate handling, instead of an unhandled unique-violation 500. The first claim stands.

## Known limitations / follow-ups — 2026-07-29

Recorded during the same adversarial review, deliberately NOT fixed in this pass (MAJOR-1, MAJOR-2
and the per-dispatch timeout / materialisation isolation above ARE fixed as of 2026-07-29):

- **MODERATE-5 — event spam dedup.** A persistently-suppressed or persistently-failing trigger emits
  `SCHEDULE.FIRE.SUPPRESSED` / `SCHEDULE.FIRE.FAILED` (and, while a backlog persists,
  `SCHEDULE.WINDOW.SKIPPED`) every tick with no dedup/cooldown, so a stuck fleet writes a steady
  drip of audit rows. Follow-up: per-(trigger, reason) emission cooldown or aggregation.
- **LOW-7 — AC1 wording vs implementation key.** AC1 promises at-most-once per
  `(tenantId, definitionId, windowKey)` but the ledger's unique index and the advisory-lock key are
  per `(triggerId, windowKey)` — two DIFFERENT trigger rows for the same tenant + definition (distinct
  `Name`s) can each fire the same window. Follow-up: either amend AC1 to say per-trigger, or add a
  definition-level uniqueness decision.
- **LOW-8 — stale-claimed-row observability.** A pod crash between ledger claim and dispatch leaves a
  `claimed` row that never becomes `dispatched`/`failed` (and, post-MAJOR-1, a burnt manual fire is a
  `claimed` row with non-null `DispatchedAt`). Nothing sweeps or surfaces these beyond retention
  pruning. Follow-up: a periodic sweep that stamps rows older than a threshold `failed` with a
  `SCHEDULE.FIRE.FAILED` event, or a metric/admin listing.
- ~~**Dead index `IX_scheduled_triggers_Enabled_NextDueAt`**~~ — **removed 2026-07-29** (no query
  filters on `NextDueAt`; the tick lists by `Enabled` + tenant). Dropped from
  `TammaModelConfiguration.cs`, the unreleased `20260729035316_AddScheduledTriggers.cs` migration
  (edited in place per the no-migration-anxiety policy; the migration now carries an idempotent
  `DROP INDEX IF EXISTS` because `scheduled_triggers` survives the Epic 19 startup wipe, so a DB that
  ran the original version still carried the index), both affected `.Designer.cs` files, and
  `ControlPlaneDbContextModelSnapshot.cs`. `dotnet ef migrations has-pending-model-changes
  -c ControlPlaneDbContext` reports none.
