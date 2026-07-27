# Implementation Plan — Story 41-30: The Tenant-Aware Scheduled-Trigger Seam

> **Read this first.** Every prior Epic 41 document treats this component as *unowned and
> unspecified*, and two of them (41-5's plan, "Design Notes — Part B"; 41-20's plan, D8) left behind a
> requirements list for whoever eventually built it. **This plan is the answer to those lists**, and it
> satisfies each bullet explicitly in D10. It also corrects a load-bearing factual claim those documents
> inherited from a source comment — see Correction 1.

## Scope & Deliverable

One `IHostedService` (`TenantScheduledTriggerService`) in `Tamma.ElsaServer`, two control-plane tables
(`scheduled_triggers` + `scheduled_trigger_fires`), one admin API, one `SCHEDULE.*` DCB event family,
and one Epic 43 catalog registration. It dispatches **any** workflow definition, per tenant, per cron
window, **at most once across the fleet**, durably.

**Not in scope:** any consumer workflow (41-11/41-16/41-17/41-20/41-23 each ship their own binding and
this story registers none of them); converting the two existing schedulers; a UI.

## Pre-Reading

- `docs/stories/epic-41/README.md` — the Wave-0 enabler table (owner *"none — must be written"*), the
  Dependencies bullet *"Scheduled workflows have no reusable pattern"* (the four-part indictment), and
  the **2026-07-25 scoping decision** ("scheduling is needed for audits, NOT for ceremonies") that
  reduced the consumer set from seven to five
- `docs/stories/epic-41/story-41-5/implementation-plan.md:160-176` — **"Design Notes — Part B (BLOCKED;
  requirements only)"**, the most complete pre-existing requirements list for this component
- `docs/stories/epic-41/story-41-20/implementation-plan.md` — D8 (the trigger-agnostic contract this
  seam must honour: `windowKey` is an **opaque string input**, the consumer derives every scoped id
  from it), Correction 3 (the indictment), Correction 4 (the `SecretAutoRotationScheduler` shape note)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` — **the
  non-pattern**, in full. `FireAtMinute` (`:34`), `_lastFired` in-process (`:83`), hardcoded target
  (`:197-204`), `ComputeAdvisoryLockKey(year, dayOfYear, hour)` with **no tenant component** (`:241-255`),
  and the reusable half: `IRollupSchedulerLeaderLock` (`:265-274`) + `PostgresAdvisoryLeaderLock`
  (`:282-366`)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Rotation/SecretAutoRotationScheduler.cs` — the
  **durable-due-time** shape (a per-row `NextRotationDueAt` in the DB, not a process field) and the
  `Enabled = false` default this story copies
- `apps/tamma-elsa/src/Tamma.Data/Repositories/QueuedTaskRepository.cs:79-100`
  (`ListPendingFromAnyTenantAsync`) — **the landed active-tenant fan-out pattern**: snapshot
  `cp.Tenants.Where(t => t.DeletedAt == null).OrderBy(t => t.Id)`, then loop per tenant with per-tenant
  failure isolation. Copy this, do not invent a second one.
- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:96-101` (`elsa.UseScheduling()`), `:141-148` (the
  rollup scheduler's options+hosted-service registration shape), `:197-215` (the **conditional**
  `ControlPlaneDbContext` registration this service must tolerate being absent)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Tamma.ElsaServer.csproj:29` —
  `<PackageReference Include="Elsa.Scheduling" Version="3.5.3" />` (**Correction 1**)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:3240-3283` — **the destructive startup DROP list**. Read the
  whole statement; AC7 exists because of it.
- `docs/stories/epic-43/story-43-3/43-3-groups-and-behaviour-preserving-defaults.md:129-130` — the
  `automation:*` / `platform-automation` grouping a new background actor lands in
- `docs/stories/epic-43/story-43-8/43-8-drift-harnesses.md:91` — the **bidirectional hosted-service
  sweep** that will go red if this service is registered without a catalog member
- **NOT FOUND (verified):** any generic scheduling seam; any `scheduled_*` entity or migration
  (`ls src/Tamma.Data/Migrations/*` + `grep` over `Entities/` — the only `schedule` hits are
  `TenantPlanAssignment`, `SecretRow.RotationSchedule` and `BillingSubscription`'s pending-downgrade);
  any `Quartz`/`NCrontab` package reference; any tenant-scoped advisory-lock key anywhere in the tree

## Corrections to the story and to the epic's standing assumptions

1. **CORRECTION — cron parsing is already a solved, in-tree dependency. The epic's premise that this
   needs new packages is false.** `HourlyAnalyticsRollupScheduler`'s own header comment (`:45-49`) calls
   itself a *"lightweight alternative to wiring a full Elsa cron-trigger activity (which would require
   additional Elsa packages)"*, and the epic README inherited that framing. **It is not true today:**
   - `Tamma.ElsaServer.csproj:29` already references `Elsa.Scheduling` 3.5.3;
   - `Program.cs:100` already calls `elsa.UseScheduling()`;
   - `Elsa.Scheduling` 3.5.3 declares `Cronos 0.11.0` as a direct dependency (verified in the package
     nuspec), and ships `Elsa.Scheduling.ICronParser` with a `CronosCronParser` implementation
     registered by the feature;
   - `AdlOrchestratorWorkflow.cs:2` already imports `Elsa.Scheduling.Activities`.

   **Consequence:** the seam needs **zero new NuGet packages**. See D3 for which of the two available
   parsers to use and why `ICronParser` alone is insufficient.

2. **CORRECTION — this does not mean Elsa's `Cron` trigger activity can replace the seam.** Having
   verified the package is present, the obvious question is "why not just use `Elsa.Scheduling.Activities.Cron`?"
   Two independent reasons, both structural:
   - **No tenant dimension.** An Elsa `Cron` trigger arms one trigger per workflow *definition*. The
     consumers need one fire per *tenant* per window over a tenant set that changes at runtime. Elsa's
     trigger indexing is keyed on definition + payload, not on a row set enumerated at fire time.
   - **`LocalScheduler` is in-process.** `Elsa.Scheduling.Services.LocalScheduler` is the shipped
     `IScheduler`; on an N-pod deploy each pod arms its own copy. That is the same multi-pod defect
     `HourlyAnalyticsRollupScheduler` had to add `pg_try_advisory_lock` to fix.

   So: use Elsa's **parser**, not Elsa's **scheduler**. Record this in the code comment, because the
   next reader will ask.

3. **CORRECTION — the story's "advisory lock" is necessary but NOT sufficient, and the epic's
   requirements lists all stop at the lock.** A `pg_try_advisory_lock` is a *session*-scoped lock: it
   is released the instant the connection closes, including by a pod crash. If the pod dies between
   `DispatchAsync` returning and any state being written, the next tick sees an unlocked, un-recorded
   window and fires again. **The lock prevents concurrent double-fire; only a committed ledger row
   prevents sequential double-fire.** Hence D2's `scheduled_trigger_fires` table with a UNIQUE
   constraint, claimed *before* dispatch. Both mechanisms ship; neither alone satisfies AC1.

4. **CORRECTION — the story's AC4 must not over-promise.** Exactly-once dispatch is impossible across a
   process boundary: a crash after the ledger commit but before `DispatchAsync` loses the fire, and a
   crash after `DispatchAsync` but before the outcome is recorded may re-fire on the retry path. The
   honest contract is **at-most-once per window by default**, with the miss surfaced as
   `SCHEDULE.FIRE.FAILED` and swept into the next window rather than silently retried. AC1 and AC4 are
   worded for at-most-once; do not "strengthen" them.

5. **NEW — the destructive startup DROP list is a live hazard for this feature, and it is not
   hypothetical.** `Tamma.Api/Program.cs:3240-3282` drops ~50 control-plane tables on every boot unless
   `TAMMA_PRESERVE_DB=1`. A schedule table caught by that list means **every deploy silently disables
   every tenant's audits** — the exact failure mode an audit exists to prevent. Epic 43's 43-5 made the
   same call for `action_assignments` ("DELIBERATELY EXCLUDED from the destructive startup DROP list").
   This story follows it, and AC7 pins it with a test that greps the SQL literal.

6. **NEW — `ControlPlaneDbContext` is registered *conditionally* in `Tamma.ElsaServer`.**
   `Program.cs:197-215` wires it only when `ConnectionStrings:ControlPlane` or `:DefaultConnection` is
   set, and short-circuits with a logged disabled-message otherwise. The service registration must sit
   **inside that same `if`** (the `TenantCleanupRequestedTrigger` precedent at `:210-215`), or a dev
   composition without a control plane fails DI at startup. D5.

7. **NEW — there is no `repository` dimension on the schedule, and the consumers need one.** 41-20
   scopes ids as `{repository}#{windowKey}#{lens}`; 41-17's PR sweep is per-repository by nature. The
   tenant→repository set lives in `github_installation_repos` / `TenantPlatformInstallation`, is
   platform-specific, and changes independently of the schedule. **Decision (D6): the seam fires once
   per `(tenant, trigger)`, not once per repository.** Repository fan-out is the *consumer* workflow's
   job — it already reads its own repo set and it is the layer that knows which repos its audit applies
   to. Putting a repo dimension in the schedule row would make the ledger cardinality
   tenant×repo×window and couple the seam to the git-platform abstraction. Consumers that want a
   per-repo cadence pass a repo filter in `input_json`.

## Design Decisions

- **D1 — `scheduled_triggers` (control plane).** Columns: `id` (uuid pk), `tenant_id` (uuid, **nullable**
  — null = platform default template, D6b), `definition_id` (text, the Elsa workflow definition id —
  **data, never a constant**, AC3), `name` (text, stable per tenant), `cron_expression` (text),
  `enabled` (bool, default true), `input_json` (jsonb, default `{}`, merged into the dispatch inputs),
  `next_due_at` (timestamptz, null until first computed), `last_window_key` (text, null),
  `last_fired_at` (timestamptz, null), `created_at`/`updated_at`/`created_by`.
  `UNIQUE NULLS NOT DISTINCT (tenant_id, definition_id, name)` — the same idiom `prompt_overrides`
  already uses in this codebase for a nullable-principal key.
  **Residency: control plane, not tenant schema.** Same three reasons 43-5 recorded — the sweeper must
  enumerate *across* tenants (a tenant-schema table cannot be scanned without opening N connections
  first), a new tenant-schema migration does not reach already-provisioned tenants, and the
  migrate-all-provisioned-tenants sweep does not exist until Epic 44-1.

- **D2 — `scheduled_trigger_fires` (control plane) is the durable at-most-once ledger** (Correction 3).
  Columns: `id`, `trigger_id` (fk), `tenant_id`, `definition_id`, `window_key` (text),
  `claimed_at`, `dispatched_at` (nullable), `workflow_instance_id` (nullable), `outcome` (text:
  `claimed|dispatched|failed`), `detail` (nullable).
  **`UNIQUE (trigger_id, window_key)`.** The claim is `INSERT … ON CONFLICT DO NOTHING` returning row
  count: **1 = we own this window, 0 = someone already did it**. That is the whole dedupe, and it is
  correct across pods, restarts and clock skew because Postgres arbitrates it.
  Retention: prune rows older than 90 days on the same tick (bounded `DELETE … WHERE claimed_at < now()
  - interval`), so the ledger does not grow without bound.

- **D3 — cron evaluation uses `Cronos` directly, not `ICronParser`** (Correction 1). `ICronParser`
  exposes only `GetNextOccurrence(string expression)` — "next occurrence **from now**". Window
  computation needs "next occurrence strictly after an **arbitrary anchor**" (the previous window's
  instant), which `Cronos.CronExpression.GetNextOccurrence(DateTimeOffset from, TimeZoneInfo tz)`
  provides and `ICronParser` does not. `Cronos` 0.11.0 is already in the restore graph transitively via
  `Elsa.Scheduling`; add an **explicit** `<PackageReference Include="Cronos" Version="0.11.0" />` to
  `Tamma.ElsaServer.csproj` so the dependency is declared rather than inherited (no new download).
  Everything is UTC — 5-field expressions, `TimeZoneInfo.Utc`, matching the house convention.
  **`windowKey` = the ISO-8601 instant of the window's scheduled fire time**, e.g.
  `2026-07-27T03:00:00Z`. It is derived, deterministic, sorts, and is an opaque string to every
  consumer (41-20 D8's contract).

- **D4 — the lock key gets a tenant component, and the bug it fixes is pinned by name.** Reuse
  `IRollupSchedulerLeaderLock` / `PostgresAdvisoryLeaderLock` verbatim — they are correct and already
  tested; only the **key derivation** was wrong. New pure function
  `ScheduleLockKey.Compute(Guid tenantId, Guid triggerId, string windowKey)` mixing all three into a
  `long` (the `0x524C5550`-style ASCII prefix convention kept, `"SCHD"`). A test asserts two different
  tenants on the same window produce **different** keys — the regression pin for
  `HourlyAnalyticsRollupScheduler.cs:241`.
  **Order of operations per due trigger:** acquire lock → claim ledger row → dispatch → stamp
  outcome → release. The lock keeps two pods off the same window cheaply; the ledger is what makes it
  durable (Correction 3).

- **D5 — `TenantScheduledTriggerService : BackgroundService`, in `Tamma.ElsaServer`.** Placement
  rationale: `IWorkflowDispatcher` is in-process there (`HourlyAnalyticsRollupScheduler` proves the
  shape), whereas `Tamma.Api` would have to dispatch over HTTP to the engine (the
  `SecretAutoRotationScheduler` compromise). Registered **inside the conditional
  `ControlPlaneDbContext` block** (Correction 6). Options class
  `TenantScheduledTriggerOptions { bool Enabled = false; TimeSpan PollInterval = 60s; int MaxFiresPerTick = 50;
  TimeSpan LedgerRetention = 90d; }`, section `ScheduledTriggers`. **`Enabled = false` by default**
  (AC9) — the `SecretAutoRotationScheduler` precedent, not the rollup's `true`.
  Tick shape, copying `ListPendingFromAnyTenantAsync`'s isolation discipline:
  1. snapshot active tenants (`DeletedAt == null`, ordered by id);
  2. load enabled triggers whose `tenant_id` is in that set **or** is null-and-materialised (D6b);
  3. per trigger, compute due windows from `last_fired_at` (or `created_at` on first run);
  4. apply the catch-up bound (D7);
  5. lock → claim → dispatch → stamp;
  6. **one trigger's failure never aborts the tick** — log WARN, emit `SCHEDULE.FIRE.FAILED`, continue
     (the rollup's failure-isolation stance, applied per row rather than per tick);
  7. prune the ledger.
  `MaxFiresPerTick` bounds a cold start on a large fleet.

- **D6 — no repository dimension (Correction 7); platform default templates are materialised, not
  resolved at fire time.** A `tenant_id IS NULL` row is a *template*. The tick **materialises** a
  concrete per-tenant row for any active tenant that lacks one for that `(definition_id, name)`, then
  fires the concrete row. Rejected alternative: resolving the template at fire time and writing ledger
  rows against the template's id — it makes `UNIQUE (trigger_id, window_key)` collapse across tenants,
  which is the tenant-suppression bug again in a new costume. Materialising also gives a tenant a real
  row to disable.

- **D7 — bounded catch-up (AC6).** For each due trigger, compute the set of missed windows since
  `last_fired_at`. Fire **only the most recent**; emit one `SCHEDULE.WINDOW.SKIPPED` carrying
  `skippedCount` and the first/last skipped `windowKey`. Rationale: an audit's value is the *current*
  posture, not a backfill; and 24 replayed hourly audits after an outage is a thundering herd against
  the LLM egress path and the tenant's budget. The skipped windows are recorded, so the gap is
  auditable — which is the property that makes dropping them acceptable.

- **D8 — admin API + per-mode RBAC, answered separately** (CLAUDE.md's universal rule).
  `GET/POST/PUT/DELETE /api/admin/scheduled-triggers` (+ `POST /{id}/run-now`, which claims a
  synthetic `manual:{timestamp}` window key so a manual run cannot collide with a cron window).
  - **single-user mode:** the sole user owns their triggers; no RBAC beyond authentication.
  - **SaaS mode:** a `tenant_id IS NULL` **template** row is **platform-owner only**
    (`PlatformOwnerAccess`) — a tenant must not be able to write a row that materialises into every
    other tenant. A concrete `tenant_id`-scoped row is writable by `tenant_owner`/`tenant_admin` for
    **their own** tenant; `member` gets 403 on write, 200 on read. The `definition_id` field is
    validated against a **closed allowlist of schedulable definition ids** in both modes — an
    arbitrary-workflow-dispatch primitive exposed over an admin API is a privilege-escalation surface
    (a tenant admin could otherwise schedule `delete-tenant`).
  - Malformed cron ⇒ **400 at write time** with a typed error (AC5), never a fire-time throw.

- **D9 — DROP-list exclusion, pinned** (Correction 5, AC7). Add **neither** table to
  `Program.cs:3243-3282`, and add a test that reads the SQL literal (the file is in the repo; read it as
  text) and asserts `scheduled_triggers` / `scheduled_trigger_fires` do not appear. Also add the
  standing comment next to the DROP list naming the exclusion and why, so the next person adding a
  table to that list does not sweep these in.

- **D10 — every requirement the two prior plans left for this component, answered.** Reproduced here so
  the closure is checkable rather than claimed:

  | Requirement (41-5 plan `:165-176`; 41-20 plan D8) | Answered by |
  |---|---|
  | Tenant component in the advisory-lock key | D4 (+ named regression test) |
  | `tenantId` threaded into the dispatch | D5 step 5; AC4 |
  | Durable last-fired window per `(tenantId, definitionId, windowKey)` — not a process field | D1 (`last_window_key`) + D2 (the ledger, which is the authoritative one) |
  | A window/cron shape, not a `FireAtMinute` int | D3; AC5 |
  | Not hardcoded to one target workflow; options not hardcoded to one config key | D1 (`definition_id` is data); D5 (one options section for all schedules); AC3 |
  | Idempotency must not assume the target is UPSERT-idempotent | D2 — the ledger is upstream of the target and makes no assumption about it; AC4 states the boundary |
  | Consumers to satisfy simultaneously: 41-11, 41-16, 41-17 (PR sweep), 41-20, 41-23 | All five take `tenantId` + opaque `windowKey` + `input_json`; the seam is target-agnostic |
  | `windowKey` is an opaque string the consumer scopes ids from (41-20 D8) | D3 — an ISO instant string; the seam never parses it back |

- **D11 — rejected alternative: a "sweep coordinator" Elsa workflow doing the fan-out.** Tempting
  (durable per-step state for free) and wrong here. It would put the fan-out on the workflow-execution
  path, where the epic's own `PlatformTaskWorker` hazard already lives (CLAUDE.md's *"V2 Cranl saga
  requires ≥2 platform-worker processes"* — a saga block-polling an inner task on the same single-slot
  queue). A coordinator workflow dispatching N child audits and awaiting them reproduces that shape
  exactly. The hosted service dispatches fire-and-forget; the ledger, not a workflow instance, is the
  durable state.

## Implementation Steps

1. **Precondition check (no code).** `dotnet build` green. Confirm: `Elsa.Scheduling` referenced and
   `UseScheduling()` called (Correction 1); `ControlPlaneDbContext` conditionally registered in
   `Tamma.ElsaServer` (`Program.cs:197-215`); `IRollupSchedulerLeaderLock` present; the DROP list at
   `Tamma.Api/Program.cs:3243`. **If a scheduling seam has since landed, stop and revisit — do not build
   a second one.**

2. **CREATE** `apps/tamma-elsa/src/Tamma.Data/Entities/ScheduledTrigger.cs` and
   `ScheduledTriggerFire.cs` (D1, D2). **MODIFY** `ControlPlaneDbContext.cs` (two `DbSet`s) and
   `TammaModelConfiguration.cs` (snake_case table/column names, the two unique indexes, the
   `enabled`/`outcome` CHECK constraints — follow the `Tenant`/`TenantPlanAssignment` configuration
   shape already in that file).

3. **CREATE the control-plane migration** under `src/Tamma.Data/Migrations/ControlPlane/`
   (`AddScheduledTriggers`). Verify `dotnet ef migrations has-pending-model-changes` is clean after.

4. **MODIFY** `apps/tamma-elsa/src/Tamma.Api/Program.cs` — **do not add the two tables to the DROP
   list**; add the standing comment naming the deliberate exclusion (D9).

5. **CREATE** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ScheduleWindowCalculator.cs` —
   pure, Elsa-free, total, fail-closed. `TryParse(cron) → bool + error`;
   `DueWindows(cron, since, now) → IReadOnlyList<DateTimeOffset>`; `WindowKey(DateTimeOffset) → string`;
   `ScheduleLockKey.Compute(tenantId, triggerId, windowKey) → long` (D3, D4). **All the interesting
   logic lives here and is unit-testable without a host.**

6. **CREATE** `apps/tamma-elsa/src/Tamma.Activities/Scheduling/ScheduleEvents.cs` — the five constants
   in the house `*Events.cs` shape with `ParseTenantId` + `StatusForEvent`
   (`SCHEDULE.WINDOW.SKIPPED` and `SCHEDULE.FIRE.FAILED` are error-status).

7. **CREATE** `apps/tamma-elsa/src/Tamma.Data/Repositories/ScheduledTriggerRepository.cs` — the
   active-tenant snapshot (copied from `QueuedTaskRepository.ListPendingFromAnyTenantAsync`), the
   due-trigger load, `TryClaimFireAsync` (the `ON CONFLICT DO NOTHING` claim — **this method is the
   correctness core**), `StampOutcomeAsync`, `MaterialiseTemplatesAsync`, `PruneLedgerAsync`.

8. **CREATE** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TenantScheduledTriggerService.cs` +
   `TenantScheduledTriggerOptions` (D5). Expose an `internal Task InvokeTickForTestsAsync(CancellationToken)`
   — the exact test seam `HourlyAnalyticsRollupScheduler.cs:159-160` already uses, and
   `Tamma.ElsaServer.csproj:15` already grants `InternalsVisibleTo("Tamma.Activities.Tests")`.

9. **MODIFY** `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` — options binding + `AddHostedService`,
   **inside the conditional control-plane block** (Correction 6, D5).

10. **CREATE** `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/ScheduledTriggerEndpoints.cs` (D8) —
    including the closed schedulable-definition allowlist and the write-time cron validation.

11. **Epic 43 registration (AC8).** Add one `BackgroundActor` member
    (`automation:tenant-scheduled-trigger-service`) and three `ExternalEffect` members
    (`effect:schedule.create|update|delete`), all in `platform-automation`, and extend 43-3's
    per-group expected-set assertion (`platform-automation` 36 → 40) and the 153 grand total → 157.
    **If Epic 43 has not landed**, skip the code change and instead add a named row to 43-3's story so
    the count moves once, not twice — do **not** create a parallel vocabulary.

12. **CREATE the tests** (below). Finish with full `dotnet test` and
    `dotnet ef migrations has-pending-model-changes`.

## Data & Migrations

Two new **control-plane** tables (D1, D2) in one migration. No tenant-schema migration, so the
missing migrate-all-provisioned-tenants sweep (Epic 44-1) is not a blocker — that is the third reason
for control-plane residency. Both tables are **excluded from the destructive startup DROP list** (D9).
`SCHEDULE.*` rides the existing `TammaEventEmitter` → `EventPersistenceMiddleware` → `EventRepository`
drain; no event-schema change.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the concurrency suite).

- **`ScheduleWindowCalculatorTests`** (`Tamma.Activities.Tests`, no host) — cron parse accept/reject
  table incl. the malformed cases AC5's 400 must catch; `DueWindows` across an hour boundary, a DST
  instant (must be a no-op: everything is UTC), a leap day, and a `since` in the future (⇒ empty, not
  negative); `WindowKey` determinism + sort order; **`ScheduleLockKey` — two tenants, same trigger
  name, same window ⇒ different keys.** *That last assertion is the regression pin for
  `HourlyAnalyticsRollupScheduler.cs:241`; name the test after the bug.* **Covers AC2, AC5.**
- **`ScheduledTriggerRepositoryTests`** (Testcontainers) — **the claim race is the headline:** two
  concurrent `TryClaimFireAsync` calls for the same `(trigger, window)` ⇒ exactly one returns true;
  a third call after the first *commits* still returns false (the sequential-double-fire case,
  Correction 3); a claim for a **different** tenant's trigger on the same window succeeds. Plus
  template materialisation (idempotent, one row per active tenant, none for soft-deleted tenants) and
  bounded ledger pruning. **Covers AC1, AC2.**
- **`TenantScheduledTriggerServiceTests`** (`Tamma.Activities.Tests`, `InvokeTickForTestsAsync` +
  a fake `TimeProvider` + a capturing `IWorkflowDispatcher`) —
  (a) a due trigger dispatches **once**, with `tenantId`, `windowKey` and the row's `input_json`
  present in the dispatch inputs (**AC4**);
  (b) a second tick in the same window dispatches **nothing**;
  (c) three tenants, one schedule ⇒ **three** dispatches, three distinct lock keys (**AC2**);
  (d) `Enabled = false` ⇒ the loop returns immediately and nothing is dispatched (**AC9**);
  (e) a dispatcher that throws for tenant 2 ⇒ tenants 1 and 3 still dispatch,
  `SCHEDULE.FIRE.FAILED` emitted once (failure isolation);
  (f) **catch-up:** `last_fired_at` 24 h ago on an hourly cron ⇒ exactly **one** dispatch and one
  `SCHEDULE.WINDOW.SKIPPED` with `skippedCount == 23` (**AC6**);
  (g) `MaxFiresPerTick` bounds a cold start.
- **`ScheduledTriggerTargetAgnosticismTests`** (**AC3**) — read `TenantScheduledTriggerService.cs`
  as text and assert it contains **no** consumer `DefinitionId` literal (`security-audit`,
  `tech-debt-triage`, `pr-triage-sweep`, `capacity-review`, `regression-management`) and no reference
  to `HourlyAnalyticsRollupWorkflow.DefinitionId`. Crude, and exactly right: the failure it prevents
  is someone "just adding one" constant.
- **`ScheduleTablesNotDroppedOnStartupTests`** (**AC7**, D9) — read `Tamma.Api/Program.cs` as text,
  locate the `DROP TABLE IF EXISTS` literal, assert neither table name occurs inside it.
- **`ScheduledTriggerEndpointsTests`** (`Tamma.Api.Tests`, `WebApplicationFactory`) — **AC5**: a
  malformed cron ⇒ 400 with the typed code, and **no row is written**; **D8**: a `member` PUT ⇒ 403; a
  `tenant_admin` writing another tenant's row ⇒ 403/404; a non-platform-owner writing a
  `tenant_id: null` template ⇒ 403; a `definition_id` outside the allowlist (`delete-tenant`) ⇒ 400.
- **Drift guards** — `BackgroundActorCoverageTests` (43-8) green with the new `automation:*` member;
  43-3's `platform-automation` expected-set and grand-total assertions updated in the same commit.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — at-most-once per `(tenant, definition, window)` across the fleet, durably | 2, 3, 7 (D2) | `ScheduledTriggerRepositoryTests` claim race + post-commit re-claim |
| 2 — tenant isolation in the lock key | 5, 8 (D4) | `ScheduleWindowCalculatorTests` (the named regression pin); service test (c) |
| 3 — target-agnostic (definition id is row data) | 2, 8 (D1) | `ScheduledTriggerTargetAgnosticismTests` |
| 4 — `tenantId` + `windowKey` reach the workflow as inputs | 8 (D5) | service test (a) |
| 5 — cron shape; malformed rejected at write time | 5, 10 (D3, D8) | calculator table; endpoint 400 test |
| 6 — bounded catch-up + `SCHEDULE.WINDOW.SKIPPED` | 8 (D7) | service test (f) |
| 7 — excluded from the destructive DROP list | 4 (D9) | `ScheduleTablesNotDroppedOnStartupTests` |
| 8 — Epic 43 catalog registration | 11 | `BackgroundActorCoverageTests`; 43-3 expected-set |
| 9 — disabled by default | 8 (D5) | service test (d) |

## Risks & Mitigations

- **The obvious wrong implementation is a `HourlyAnalyticsRollupScheduler` clone**, and both prior
  plans warn about it in those words. Mitigation: the two named regression pins (tenant in the lock
  key; no target constant in the source) fail loudly on exactly that, and Correction 3 explains why
  copying its *lock* without its *ledger* replacement is still wrong.
- **The DROP-list hazard is silent and catastrophic** (Correction 5): a deploy would disable every
  tenant's audits with no error anywhere. Mitigation: AC7's text-reading test plus the standing comment
  at the DROP site.
- **An admin-writable `definition_id` is a privilege-escalation primitive.** Mitigation: D8's closed
  allowlist, enforced server-side and tested with `delete-tenant` as the negative case. Do not relax
  this to "any registered definition".
- **Catch-up policy is a product judgement, not a technical one** (D7). Mitigation: the skipped windows
  are *recorded*, so if a consumer later needs backfill the evidence exists and the policy can change
  per trigger without a schema change. Flag it in review rather than assuming.
- **Epic 43 ordering.** If 43-2/43-3 have not landed, step 11 has nowhere to register. Mitigation:
  the story's Dependencies say to file the member as a named follow-up row rather than invent a second
  vocabulary — the failure mode to avoid is a second `BackgroundActor`-shaped enum.
- **Correction 1 invalidates a sentence in the epic README and in `HourlyAnalyticsRollupScheduler`'s
  own header.** Mitigation: the README's Wave-0 row and Dependencies bullet are updated by this story;
  the source comment is left alone (this story does not touch `apps/` prose it does not otherwise edit)
  and is noted here instead.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition check | 0.25 |
| 2–4 | Two entities + model config + migration + DROP-list exclusion | 1.0 |
| 5–6 | `ScheduleWindowCalculator` + `ScheduleLockKey` + `ScheduleEvents` | 0.75 |
| 7 | Repository incl. the `ON CONFLICT` claim | 1.0 |
| 8–9 | Hosted service + registration | 1.0 |
| 10 | Admin API + per-mode RBAC + allowlist | 1.0 |
| 11 | Epic 43 catalog registration + pin bumps | 0.25 |
| 12 | Tests (calculator, repo race, service ×7, two text pins, endpoints) | 1.5 |
| **Total** | | **6.75** (story estimate 5–6 d — **revise to 6–7**; the Testcontainers claim-race suite and the per-mode RBAC surface are both larger than the story assumed) |

## Blocks / Blocked by

- **Blocked by:** nothing. Every dependency is landed and verified (story Dependencies).
- **Blocks (AC1 of each):** **41-11**, **41-16**, **41-17** (PR-triage half), **41-20**, **41-23**.
  Wave 2 in its entirety.
- **Does NOT block 41-5 / 41-7** — ceremonies are user-initiated (product-owner decision, 2026-07-25).
  `docs/sprint-status.yaml`'s 41-5 and 41-7 lines still say otherwise and are corrected alongside this
  story.
- **Interlocks with Epic 43:** 43-2 (`BackgroundActor`), 43-3 (group + count pins), 43-8 (the
  hosted-service sweep). Coordinate the count bump with any other story adding a background actor.
- **Shared-file register (coordinate before editing):** `Tamma.Data/ControlPlaneDbContext.cs` +
  `TammaModelConfiguration.cs` + `Migrations/ControlPlane/` (every control-plane story — serialize the
  migration); `Tamma.Api/Program.cs`'s DROP list (43-5 makes the same exclusion — land one comment, not
  two); `Tamma.ElsaServer/Program.cs`'s conditional control-plane block;
  43-3's `platform-automation` expected set.
- **Follow-ups deliberately not taken here:** migrating `HourlyAnalyticsRollupScheduler` onto the seam
  (platform-global by design); filling `RotationScheduleCalculator.RegisterCronEvaluator` in
  `Tamma.Api` (Story 29-2's seam, different assembly, different unit of scheduling — but D3 settles the
  parser question for it).
