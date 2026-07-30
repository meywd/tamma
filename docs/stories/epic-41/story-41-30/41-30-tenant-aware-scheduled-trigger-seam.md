# Story 41-30: The Tenant-Aware Scheduled-Trigger Seam

Status: done — conformance-reviewed 2026-07-29; the hosted service, both control-plane tables, the admin API and the DROP-list exclusion all ship (`Enabled=false` by default, so no running deployment changes); AC1's at-most-once key is per-trigger, not per-(tenant, definition) — a deliberate, now-documented decision (LOW-7, resolved 2026-07-30); MODERATE-5 and LOW-8 are also resolved as of 2026-07-30, so the three gates on turning the seam on are closed

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
   `input_json` as workflow inputs. *[2026-07-30]* The tick additionally runs a **bounded stale-claim
   sweep** (LOW-8) that burns and announces ledger rows abandoned by a dead pod.
4. **Cron evaluation with no new dependency** — `Cronos` is already in the graph (Correction 1).
5. **Catch-up policy, bounded** — a tenant whose engine was down for a day does not get 24 audits. One
   run for the most recent missed window, and **one** `SCHEDULE.WINDOW.SKIPPED` event naming the count
   and the window range (first/last key) of the windows dropped, so the gap is *auditable* rather than
   invisible.
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
`SCHEDULE.FIRE.SUPPRESSED` (a CONCURRENT pod won the window — INFO, not an error. *[Amended
2026-07-30, MODERATE-5]* NOT emitted when re-observing a window that already reached a terminal ledger
outcome; that case is silent),
`SCHEDULE.WINDOW.SKIPPED` (LOUD — catch-up dropped a window, with the count. *[Amended 2026-07-30,
MODERATE-5]* emitted **after the ledger claim for the firing window is won**, so a given
`(trigger, window)` emits it at most once fleet-wide. `skippedCount` counts every window since the last
SUCCESSFUL dispatch that this trigger did not run, which includes windows that were attempted and burnt —
each of those carries its own `SCHEDULE.FIRE.FAILED`),
`SCHEDULE.FIRE.FAILED` (LOUD — dispatch threw or timed out. *[Amended 2026-07-29]* the ledger row is
**stamped `failed` and the window is burnt** — the NEXT window is the recovery path; there is no
same-window retry. The original "row is released so the next tick retries" wording contradicted the
at-most-once contract the implementation ships),
`SCHEDULE.FIRE.ABANDONED` (LOUD — *[Added 2026-07-30, LOW-8]* the stale-claim sweep found a ledger row
still `claimed` past the threshold: the pod that won the claim died before it could stamp an outcome. The
sweep burns the row and emits this once, fleet-wide. Distinct from `SCHEDULE.FIRE.FAILED` so "attempted
and threw" and "vanished with its pod" stay separable),
`SCHEDULE.TRIGGER.CHANGED` (an admin created/updated/disabled a schedule).

**One window, one audit row** *[Added 2026-07-30, MODERATE-5]* — there is no same-window retry anywhere
in this seam, and every fire-path emission hangs off a state transition that can happen only once per
`(trigger, window)`: the skip audit off the won claim, the terminal audit off the outcome stamp, the
abandonment audit off the sweep's CAS. So a window contributes **at most one** terminal row
(`DISPATCHED` | `FAILED` | `ABANDONED`) plus at most one `WINDOW.SKIPPED`. Repeated rows for one
`windowKey` therefore always mean genuine concurrency, never a retry loop — a run of `FIRE.SUPPRESSED`
rows is bounded by how long a claim stays in flight and, for an abandoned claim, by the sweep threshold.

## Autonomy behavior

None — this component makes no quality decision and produces no document, so the autonomy dial does not
apply to *it*. The dial governs the workflows it dispatches, unchanged. What Epic 43 *does* own here is
(a) the background actor itself as an `automation:*` catalog member and (b) the schedule mutations as
`effect:schedule.*` members (D8).

## Acceptance Criteria

1. **At most one dispatch per `(triggerId, windowKey)` across the whole fleet, durably.**
   Proven with two pods racing the same window, and again across a process kill between the ledger
   claim and the dispatch.

   *[Amended 2026-07-30 — LOW-7 resolved. This AC originally said `(tenantId, definitionId, windowKey)`,
   which the code has never done and now deliberately will not. **A trigger row IS a schedule.** The
   registry's natural key is `(tenant_id, definition_id, name)` precisely so one tenant can run one
   definition on two cadences with two `input_json` payloads; folding `definition_id` into the ledger key
   would make one of those schedules silently swallow the other whenever their windows coincide —
   dropping configured work, which is a worse failure than the duplicate it prevents. So the shipped key
   stays `(triggerId, windowKey)` for both the ledger's unique index and the advisory lock.*

   ***What a consumer can actually rely on*** — stated plainly, so this AC stops promising what the code
   does not do:
   - The seam **guarantees** that a given **schedule** (trigger row) invokes its target **at most once per
     window**, fleet-wide, durably across pods, restarts and clock skew.
   - The seam does **not** guarantee at-most-once per `(tenantId, definitionId, windowKey)`. If an operator
     creates two enabled schedules in one tenant for the same definition and their cron windows coincide,
     the consumer **is** invoked twice for that instant — same `tenantId`, same `windowKey`, different
     `triggerId`, usually different merged `input_json`. That is a supported configuration, not a defect.
   - Both discriminators are on the wire: every dispatch carries `tenantId`, `definitionId`, `windowKey`
     **and `triggerId`** as inputs. A consumer wanting per-schedule semantics keys its idempotency on
     `(tenantId, triggerId, windowKey)`; one wanting definition-level semantics keys on
     `(tenantId, definitionId, windowKey)` and treats the second call as the replay AC4 already obliges it
     to tolerate (41-20 D3) — in which case the second schedule's distinct inputs are intentionally ignored,
     so do not configure two overlapping schedules for such a consumer.

   Pinned by `TwoSchedules_ForOneTenantAndDefinition_EachFireTheSharedWindow_TheKeyIsPerTrigger`.]*
2. **Tenant isolation.** Tenant A's fire never suppresses tenant B's for the same window — the exact
   defect at `HourlyAnalyticsRollupScheduler.cs:241`, pinned by a regression test that fails if a
   tenant component is ever dropped from the lock key.
3. **Target-agnostic.** The workflow definition id is **row data**, never a compile-time constant. A
   test asserts no `DefinitionId` constant of any consumer workflow appears in the service's source.
4. **`tenantId`, `windowKey`, `triggerId` and `definitionId` reach the dispatched workflow as inputs**,
   and a workflow dispatched twice with the same `windowKey` is the consumers' own idempotency contract
   (41-20 D3) — this story guarantees a given **schedule** does not *call* it twice for one window, and
   states plainly that it does not guarantee the target is idempotent. *[Clarified 2026-07-30, LOW-7 —
   "not called twice" is scoped to the trigger row; `triggerId` is on the wire so a consumer can pick
   per-schedule or per-definition idempotency. See AC1.]*
5. **Cron shape, not a `FireAtMinute` int**; standard 5-field expressions evaluated in UTC; a malformed
   expression is rejected **at write time** by the admin API with a typed error, never at fire time.
6. **Bounded catch-up.** After a 24-hour outage an hourly schedule fires **once**, and emits
   `SCHEDULE.WINDOW.SKIPPED` naming the number of windows dropped.
7. **The two tables are excluded from the destructive startup DROP list** (the `DROP TABLE IF EXISTS …
   CASCADE` literal in `Tamma.Api/Program.cs` — line numbers deliberately not pinned, they drift; the
   pin test locates the statement dynamically) — a deploy must not silently disable every tenant's
   audits. Pinned by
   `ScheduledTriggerSourcePinTests.Schedule_Tables_Are_Not_In_The_Destructive_Startup_DropList`, which
   reads the DROP statement and asserts neither table name appears (D9).
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

## Resolved follow-ups — 2026-07-30

The three items left open by the 2026-07-29 adversarial review are closed. They were the stated gate on
turning the seam on (`ScheduledTriggers:Enabled=true`), and with them the five blocked consumer stories
(41-11 / 41-16 / 41-17 / 41-20 / 41-23) are unblocked. **No schema change was required** —
`dotnet ef migrations has-pending-model-changes` is clean for both `ControlPlaneDbContext` and
`TenantDbContext`, and the shipped `20260729035316_AddScheduledTriggers` migration is untouched.

### MODERATE-5 — duplicate audit-event spam after a failed dispatch — **FIXED**

**What it actually was.** `since` for the window computation is the trigger row's `LastFiredAt`, which
only advances on a **successful** dispatch. So a window whose dispatch failed kept recomputing as the due
window for the rest of its cadence — a whole day for a daily schedule. Every 60-second tick, on every pod,
re-emitted the same `SCHEDULE.WINDOW.SKIPPED` (it was emitted **before** the lock and claim), re-took the
advisory lock, lost the ledger claim and wrote another `SCHEDULE.FIRE.SUPPRESSED`: up to ~1440 duplicate
rows per day per pod for one failed window. Correctness was never at risk — the committed ledger row is
what refuses the re-dispatch — but the audit trail, which is the entire point of the `SCHEDULE.*` family,
degraded into noise.

**What shipped**, three pieces, each hanging an emission off a transition that can only happen once per
`(trigger, window)`:

1. **Settled-window short circuit, before the lock.** `IScheduledTriggerRepository.GetFireOutcomeAsync
   (triggerId, windowKey)` probes the ledger's unique index up front. A window already `dispatched` or
   `failed` is re-observed **silently** — DEBUG log, no event, no advisory lock, no claim attempt. This
   also removes the per-tick lock churn a stuck trigger used to generate.
2. **`SCHEDULE.WINDOW.SKIPPED` moved to after the claim is won.** The ledger claim is this seam's
   at-most-once arbiter, so hanging the skip audit off it gives the audit the same guarantee the fire
   has: exactly one skip row per `(trigger, window)`, fleet-wide, however many pods tick and however long
   the backlog persists.
3. **`SCHEDULE.FIRE.SUPPRESSED` now means genuine concurrency only.** With (1) in place the suppression
   paths (advisory lock held, claim lost) are reachable only while a claim is actually in flight; and a
   claim orphaned by a dead pod is terminated by the LOW-8 sweep below, so even that drip is bounded by
   the sweep threshold instead of by the schedule's cadence.

**Skipped vs retried, for an operator reading the stream.** There is no same-window retry anywhere in the
seam. A window contributes at most one terminal row (`DISPATCHED` | `FAILED` | `ABANDONED`) plus at most
one `WINDOW.SKIPPED`. So repeated rows for one `windowKey` always mean concurrency, never a retry loop.
One honest caveat recorded rather than hidden: because `LastFiredAt` still only advances on success,
`skippedCount` counts every window since the last **successful** dispatch that the trigger did not run —
including windows that were attempted and burnt. Each of those carries its own `SCHEDULE.FIRE.FAILED`, so
the two are distinguishable; the event's meaning is documented on `ScheduleEvents.WindowSkipped`.

**Tests.** `AFailedWindow_TickedRepeatedly_EmitsItsSkipAndFailure_Once_NotOncePerTick` ticks 120 times
across one failed daily window and asserts **exactly 2** audit rows total (one SKIPPED, one FAILED), zero
SUPPRESSED, one dispatch attempt and one advisory-lock acquisition — pre-fix that run produced 240.
`AnOrphanedClaim_OnTheDueWindow_Drips_Suppression_OnlyUntilTheSweepBurnsIt` proves the stream reaches a
steady state (event count after 10 ticks == after 30). `SecondTick_InTheSameWindow_Dispatches_Nothing`
was updated to assert the new silence. `GetFireOutcome_Returns_TheLedgerRowsOutcome_And_Null_WhenTheWindow
IsUnclaimed` covers the probe against real Postgres.

### LOW-7 — AC1's key vs the shipped key — **RESOLVED as option (b): keep the per-trigger key**

**Decision: (b).** The ledger's unique index and the advisory-lock key stay `(triggerId, windowKey)`; AC1
has been rewritten to state that guarantee and to spell out what a consumer can rely on (see AC1 above).

**Why not (a).** Folding `definition_id` into the key would be a correctness regression, not a
tightening. The registry's natural key is `(tenant_id, definition_id, name)` **specifically** so one
tenant can run one definition as two schedules — e.g. a nightly full `security-audit` and an hourly
changed-files one, with different `input_json`. Under (a) the two collide whenever their windows coincide
(every night at midnight, for that example) and one is silently dropped: configured work vanishing with
no event, which is worse than the duplicate invocation it would prevent. It would also collide two
different schedules' same-millisecond `manual:{timestamp}` run-now claims, and it would require a
migration to a key the rest of the design (per-trigger advisory lock, per-trigger `LastFiredAt`
bookkeeping, per-trigger run-now) does not use.

**Why (b) is defensible.** AC4 already makes idempotency under a replayed `windowKey` the consumer's
contract (41-20 D3), and both discriminators are already on the wire — every dispatch carries `tenantId`,
`definitionId`, `windowKey` **and `triggerId`**. A consumer picks its own scope. The AC now says so
instead of promising a fleet-wide property the code does not implement.

**Test.** `TwoSchedules_ForOneTenantAndDefinition_EachFireTheSharedWindow_TheKeyIsPerTrigger` pins the
shipped behaviour: two schedules, one tenant, one definition, one window ⇒ two dispatches, same
`windowKey`, same `tenantId`, distinct `triggerId`s, distinct advisory-lock keys.

### LOW-8 — the claim-then-crash contract had no surface — **FIXED**

**What it actually was.** `ScheduledTriggerFire`'s doc promised a lost fire was "surfaced as a claimed row
that never became dispatched plus `SCHEDULE.FIRE.FAILED` where detectable". For the crash case that was
false in both halves: the process that would emit the event is dead, and **nothing** — no sweep, metric,
log or alert — ever inspected stale `claimed` rows. The only way to find one was manual SQL against the
ledger.

**What shipped.** A bounded stale-claim sweep at the end of each tick
(`TenantScheduledTriggerService.SweepStaleClaimsAsync`):

- `ListStaleClaimedFiresAsync(cutoff, limit)` reads rows still `Outcome = 'claimed'` whose `ClaimedAt`
  predates `now - StaleClaimThreshold`, oldest first, at most `MaxStaleClaimsSweptPerTick` per tick.
- `TryMarkFireAbandonedAsync(fireId, detail)` is a conditional CAS (`UPDATE … SET "Outcome" = 'failed',
  "Detail" = … WHERE "Id" = … AND "Outcome" = 'claimed'`) — Postgres picks the one pod that owns the
  announcement. `DispatchedAt` is deliberately left alone so a burnt **manual** fire keeps its drain CAS
  marker (that case shows up as `dispatchAttempted: true` in the event data).
- On a won CAS: a WARN log naming fire / trigger / tenant / definition / window / claim age, plus a
  distinct `SCHEDULE.FIRE.ABANDONED` DCB event (LOUD status, `retried: false`).

**Explicitly not a retry.** At-most-once means the window is burnt; the sweep only stamps and reports, and
the next window is the recovery path. Stamping the terminal outcome is also what makes the sweep
**emit-once** — a swept row leaves the feed permanently, so the new surface cannot become the very
per-tick drip MODERATE-5 was about.

**New options on the existing `TenantScheduledTriggerOptions`** (section `ScheduledTriggers`):
`StaleClaimThreshold` (default **15 min**; must exceed `DispatchTimeout`'s 30 s so a legitimately
in-flight claim is never burnt — pinned by a test; non-positive disables the sweep) and
`MaxStaleClaimsSweptPerTick` (default **50**, so a pod that died holding hundreds of claims is announced
over several ticks rather than in one burst).

**Tests.** `AStaleClaimedRow_IsBurnt_AndAnnounced_Once_As_FireAbandoned` (burn + event shape + emit-once
across three ticks + never re-dispatched), `AnInFlightClaim_YoungerThanTheThreshold_IsNeverSwept`,
`TheStaleClaimSweep_Is_Bounded_PerTick`, `ANonPositive_StaleClaimThreshold_DisablesTheSweep`,
`Options_StaleClaimSweep_Defaults_Exceed_TheDispatchTimeout`, and against real Postgres
`ListStaleClaimedFires_Returns_OnlyClaimedRowsOlderThanTheCutoff_OldestFirst_Bounded` +
`TryMarkFireAbandoned_EightConcurrentSweeps_ExactlyOneWins_AndTheRowLeavesTheFeed`.

## Known limitations / follow-ups — 2026-07-29

Recorded during the 2026-07-29 adversarial review. MAJOR-1, MAJOR-2, the per-dispatch timeout and the
materialisation isolation were fixed the same day; **MODERATE-5, LOW-7 and LOW-8 were closed 2026-07-30 —
see "Resolved follow-ups" above.** Nothing in this section is open.

- ~~**MODERATE-5 — event spam dedup.**~~ — **FIXED 2026-07-30.** A persistently-suppressed or
  persistently-failing trigger emitted `SCHEDULE.FIRE.SUPPRESSED` / `SCHEDULE.WINDOW.SKIPPED` every tick
  with no dedup, so a stuck fleet wrote a steady drip of audit rows.
- ~~**LOW-7 — AC1 wording vs implementation key.**~~ — **RESOLVED 2026-07-30** as option (b): the
  per-trigger key is the intended design and AC1 now states it.
- ~~**LOW-8 — stale-claimed-row observability.**~~ — **FIXED 2026-07-30** by the bounded stale-claim
  sweep + `SCHEDULE.FIRE.ABANDONED`.
- ~~**Dead index `IX_scheduled_triggers_Enabled_NextDueAt`**~~ — **removed 2026-07-29** (no query
  filters on `NextDueAt`; the tick lists by `Enabled` + tenant). Dropped from
  `TammaModelConfiguration.cs`, the unreleased `20260729035316_AddScheduledTriggers.cs` migration
  (edited in place per the no-migration-anxiety policy; the migration now carries an idempotent
  `DROP INDEX IF EXISTS` because `scheduled_triggers` survives the Epic 19 startup wipe, so a DB that
  ran the original version still carried the index), both affected `.Designer.cs` files, and
  `ControlPlaneDbContextModelSnapshot.cs`. `dotnet ef migrations has-pending-model-changes
  -c ControlPlaneDbContext` reports none.
