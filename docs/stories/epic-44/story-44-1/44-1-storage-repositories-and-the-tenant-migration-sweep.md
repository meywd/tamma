# Story 44-1: Storage, Repositories, and the Migrate-All-Provisioned-Tenants Sweep

Status: done — conformance-reviewed 2026-07-29; entities, the `AddTrackerCore` tenant migration, the repositories and the `POST /api/admin/tenants/migrate` sweep all ship (the Architectural Context section is pre-implementation prose, now marked as such); sweep-endpoint hygiene CLOSED 2026-07-30 (a bare POST is now a dry run and applying needs an explicit `?apply=true` plus a confirmation header, `?dryRun=false` is refused loudly rather than silently reinterpreted; cluster-wide single-flight via a session-scoped advisory lock so a crashed pod cannot wedge the gate; apply returns 202 with a pollable run instead of sweeping inside the request; migration DDL got its own 900s timeout at the EF layer with the runtime pool's 30s proven unchanged). The `SweepAsync` seam also lost its `dryRun = false` default, so the dangerous call cannot be written by omission. The second bullet — no HTTP authorization test — was already stale: `TenantMigrationEndpointAuthTests` landed in wave 4 and drives the real JWT pipeline. Remaining, deliberate and documented: run state is per-instance, so on a multi-pod deploy a status poll may reach a different pod and get a self-explaining 404 plus a read-only `pg_locks` probe telling it a sweep is running somewhere; making that cluster-visible needs a control-plane table and is not built

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **platform operator**,
I want the tracker's tables to live in each tenant's own schema, and I want a way to apply a new tenant migration to tenants that already exist,
So that a customer's backlog is isolated by the same mechanism as their documents and events — and so that shipping the tracker does not break every tenant provisioned before the deploy.

## Priority

P0 — Wave 0. Nothing in Epic 44 reads or writes without this. **The sweep half is a platform fix, not tracker scope**, and it is here because Epic 44 is the first feature that cannot ship without it.

## Architectural Context (state at drafting, pre-44-1) (READ FIRST)

*Everything in this section is written in the present tense of 2026-07-25, before this story landed. It
describes the gap the story closes — it is not a description of the current tree. The sweep, the admin
endpoint and the data-source migrator flavour all exist as of 2026-07-29.*

- **The migration-reach gap is real and has no operational escape hatch today.** `ITenantDbMigrator.MigrateTenantAppAsync` (`apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantDbMigrator.cs:33`, impl `Tamma.Data/Pooling/EfTenantDbMigrator.cs:26`) has **exactly two production call sites**, both creation-only:
  - `Tamma.Api/Services/Provisioning/TenantProvisioningService.cs:172`, inside `ProvisionAsync` (`:147`), reached only from `Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs:176-177`. It runs **only when `password is not null`** (a freshly minted role); an idempotent re-run explicitly skips it (`:167-176`).
  - `Tamma.Activities/TenantLifecycle/MigrateTenantDatabaseActivity.cs:53`, step 4 of `CreateTenantWorkflow` (`Tamma.ElsaServer/Workflows/CreateTenantWorkflow.cs:158-160`).
  There is no admin endpoint (`Tamma.Api/Endpoints/Admin/` contains no `migrate` handler), no hosted service and no queued-task handler that fans DDL over tenants. `Program.cs:3278` migrates the **control plane** only.
- **`EfTenantDbMigrator` is already idempotent and safe to call repeatedly.** It derives the schema from the connection string's `Search Path` (`:47`), forces `Pooling=false` on the migration connection (`:51-63`), pins the per-tenant history table `__TenantMigrationsHistory` (`:64-67`), runs an idempotent `CREATE SCHEMA IF NOT EXISTS` safety net (`:83-92`) and then `MigrateAsync` (`:97`). **The sweep is a caller, not a redesign.**
- **Tenant connection resolution:** `Tamma.Data/Pooling/LruPooledTenantConnectionResolver.cs` — `GetDataSourceAsync:233`, `LeaseAsync:340`, `EvictAsync:495`. Registered singleton at `Pooling/TenantConnectionPoolServiceCollectionExtensions.cs:134-139`. Schema naming `Pooling/TenantNaming.cs:57` (`t_<hex>`); search path set on the connection string at `Pooling/TenantDatabasePool.cs:175-181`; DbContext wiring at `Tamma.Data/TenantDbContextFactory.cs:46-68`.
- **The residency precedent:** every operational tenant table is tenant-resident — `document_instances` (`Migrations/Tenant/20260722180002_AddDocumentInstances`), `channel_outbox` (`20260722211145_AddChannelOutbox`), `acceptance_rules_overrides` (`20260722011909_AddAcceptanceRulesOverrides`).
- **The XOR form to copy — and the one NOT to copy.** `20260722011909_AddAcceptanceRulesOverrides.cs:32` is the strong form `(A NOT NULL AND B NULL) OR (A NULL AND B NOT NULL)`, paired with a unique index carrying `.Annotation("Npgsql:NullsDistinct", false)` (`:35-40`). `ck_audit_records_principal_xor` (`Migrations/Tenant/20260619003624_AddAuditRecords.cs:42`) is the **weak** form `NOT (both NOT NULL)` and permits both NULL — do not copy it.
- **The isolation guarantee this residency preserves:** `apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/SchemaPerTenantMigrationTests.cs:82-86`.
- **39-20 will own `repositories`** (`docs/stories/epic-39/story-39-20/implementation-plan.md:57-60`, control-plane resident per its D1 `:32`). Epic 44 must **not** create a second repo registry.

## Acceptance Criteria

1. **Entities and DbSets.** `WorkItemEntity`, `ProjectEntity`, `IterationEntity` and `TrackerPreference` in `apps/tamma-elsa/src/Tamma.Data/Entities/`, with `DbSet`s on `TenantDbContext` and EF configuration in `TammaModelConfiguration.cs`. `WorkItemEntity` carries at minimum: `Id` (UUIDv7), `ProjectId`, `Key` (the `WorkItemRef.ToWire()` string — **frozen at creation, never re-minted on a project move**, 44-0 AC8), `PreviousKeys` (`text[]`, default `{}`; lookup by key resolves current-or-previous), `Number`, `Kind`, `Status`, `Priority` (**nullable** — `null` is "unprioritised", 44-0 AC11), `IssueType`, `Title`, `Description`, `ParentId`, `IterationId`, `Rank`, `SiblingRank` (order under the parent — 44-0 AC10; both rank columns `COLLATE "C"`), `AssigneeUserId`, `CreatedByUserId`, `Estimate` (`numeric NULL`, scale-free — the scale is `ProjectEntity.EstimateScale`, 44-0 AC13; **not** `EstimateHours`), `ExternalRefJson`, `CreatedAt`, `UpdatedAt`, `ClosedAt`, `Version`.
   Plus a small **`work_item_relations`** table — `(SourceId, TargetId, Kind)` over 44-0's `WorkItemRelationKind` `{blocks, duplicate, related}`, with a unique index on the triple. `blocks` is directed source→target; `duplicate`/`related` are symmetric and stored canonically with the lower id first so a mirror duplicate cannot be inserted. Validation is 44-3's. Rationale: `blocked` is a status with no record of *what* blocks it, and without an edge, dependency gets encoded as parenting and corrupts the tree (epic README D12).

2. **Tenant migration `AddTrackerCore`** creating `projects`, `work_items`, `iterations`, `tracker_preferences` in the tenant schema.

3. **Constraints mirror the Core vocabulary.** `ck_work_items_status` and `ck_work_items_kind` enumerate exactly the wire strings of 44-0's `WorkItemStatus` (**8** members, `triage` included) and `WorkItemKind` (**4** members — no `bug`, no `chore`; those live on the `IssueType` axis); a test asserts constraint and enum agree member-for-member. `ck_tracker_preferences_principal_xor` uses the **strong** XOR form.

4. **`work_items."Rank"` is created `COLLATE "C"`**, and a Testcontainers test inserts ranks generated by `Rank.Between` and asserts `ORDER BY "Rank"` matches `OrderBy(x => x, StringComparer.Ordinal)`. Without this the API order and the board order diverge silently (44-0 D7).

5. **Key uniqueness and monotone numbering.** `(ProjectId, Number)` is unique; `Key` is unique per tenant. Numbers are minted from a per-project counter under the row lock, so two concurrent creates cannot produce the same key — proven by a concurrency test.

6. **Repositories with parallel, never-joined mode surfaces for `tracker_preferences`.** `ITrackerPreferenceRepository` exposes `GetAsync(Guid? userId)` / `GetByTenantAsync(Guid tenantId)`, `UpsertAsync` / `UpsertForTenantAsync`, `DeleteAsync` / `DeleteByTenantAsync` — the `IAcceptanceRulesRepository.cs:5-12` contract ("The two surfaces are PARALLEL — no method silently joins both planes"), with predicates written as `p.UserId == userId && p.TenantId == default(Guid?)` and the mirror, exactly as `AcceptanceRulesRepository.cs:34,108`.

7. **`IWorkItemRepository` / `IProjectRepository` / `IIterationRepository`** carry **no** mode split — work items are tenant-schema content, not per-principal configuration (epic Decisions D6). A test asserts no work-item query filters on a `UserId` ownership plane.

8. **`POST /api/admin/tenants/migrate`** — platform-owner-only (`PlatformOwnerAccess`), enumerates `tenants`, resolves each through `LruPooledTenantConnectionResolver` and calls `EfTenantDbMigrator.MigrateTenantAppAsync`. Returns a per-tenant result list (`migrated` / `already-current` / `failed` + reason). One tenant's failure never aborts the sweep. Bounded concurrency, and a `dryRun=true` mode reporting the pending-migration count per tenant without applying.

9. **The sweep is idempotent and proven so.** A Testcontainers test provisions two tenants, applies the tracker migration to one by the creation path, runs the sweep, and asserts: the second tenant gains the tables, the first is reported `already-current` and is byte-unchanged, and a second sweep is a no-op.

10. **`projects.RepositoryId` is nullable and is a plain `Guid?`, not an FK**, documented as pointing at 39-20's control-plane `repositories` row once that lands. No second repo registry is created (epic boundary table).

## Technical Notes

- The sweep is ~40 lines of orchestration over machinery that already exists and is already idempotent. Its cost here is almost entirely the test that proves it.
- `ExternalRefJson` is a `jsonb` column, not four columns, because 44-8 owns its shape and this story must not freeze it. Null for native items.
- `Version` is an optimistic-concurrency counter bumped on every write, mirroring `AcceptanceRulesOverride.Version` (`Tamma.Data/Entities/AcceptanceRulesOverride.cs:26`). 44-2 returns it as an ETag.
- `iterations` is created here but is only populated by 44-4; the migration is single-shot so all four tables land together rather than forcing a second tenant migration two stories later. Tenant migrations are the scarcest resource in this repo — the sweep makes them survivable, not free.
- Do **not** add `IssueNumber` population for native items. `domain_events.IssueNumber` is `integer NULL` with two indexes (`Migrations/Tenant/20260610013731_InitialTenant.cs:102,462-466`); a `PROJ-123` key cannot populate it and everything already queries `Tags->>'issueId'`.

## Dependencies

- **Story 44-0** — the entities bind `WorkItemKind`, `WorkItemStatus`, `WorkItemRef`, `Rank`. Blocking.
- **Existing, no change required:** `EfTenantDbMigrator`, `LruPooledTenantConnectionResolver`, `TenantDbContextFactory`, `TammaModelConfiguration`, `PlatformOwnerAccess` (`Program.cs:1538`).
- **Blocks:** 44-2, 44-3, 44-4, 44-5, 44-7, 44-8, 44-9.
- **Adjacent:** 39-20's `repositories` table (AC10 is written to be forward-compatible with it and to avoid duplicating it).

## Out of Scope

- Any endpoint other than the sweep — 44-2.
- Hierarchy validation, ranking operations, board queries — 44-3 / 44-4.
- Events — 44-5.
- `ExternalRef`'s shape and any platform call — 44-8.
- Making the sweep automatic on startup. It is an explicit admin action in v1; an automatic sweep over N tenants at boot is a deploy-time outage risk and needs its own design.

## Estimated Effort

6 days

## Follow-ups from adversarial review (2026-07-29)

Fixed in this lane (see `.dev/bugs/2026-07-29-ef-migrator-service-provider-explosion.md` for the HIGH finding):

- **EF internal-service-provider explosion (HIGH)** — `EfTenantDbMigrator`'s data-source path passed the `NpgsqlDataSource` into `UseNpgsql`, minting one EF internal provider per swept tenant; EF throws at the 21st and the cap is process-global. Now migrates over a borrowed connection (the `TenantDbContextFactory` pattern); regression-pinned at 25 tenants + same-process re-run.
- **Rekey collision guards** — rekey onto an existing current key is a typed `TRACKER.KEY_CONFLICT` (pre-check + 23505 catch); rekey into the item's own project's future mint space advances `NextNumber` past the target under the `FOR UPDATE` lock so minting can never wedge; rekey into a **different** existing project's prefix is rejected (`TRACKER.CROSS_PROJECT_REKEY`) — a cross-project rekey is a move, not a rename, and moves never re-mint keys (out of scope for this seam by contract).
- **`GetByKeyAsync` determinism** — the current-Key match always wins; previous-keys containment is only a fallback (with an Id-ordered tie-break), so a key that is one row's current key and another row's history entry resolves to the current holder.
- **Relation-add race** — `AddRelationAsync` is insert-first; the unique-index loser (23505) returns the stored row, making the documented idempotent contract hold under concurrency. `TrackerPreferenceRepository`'s first-upsert race retries once as an update of the winner's row.
- **`DefaultKind` wire validation** — junk fails as typed `TRACKER.UNKNOWN_KIND` at the write boundary, never as raw 23514 off `ck_tracker_preferences_default_kind` (`BoardGroupBy` stays freeform by design).
- **Sweeper OCE isolation** — an `OperationCanceledException` from one tenant's own stack no longer aborts the whole sweep; it is only treated as sweep-cancellation when the sweep's token is actually canceled.
- **`WorkItemEntity.Version` is now the EF optimistic-concurrency token** *(done 2026-07-29)* — `TammaModelConfiguration` configures `Version` with `IsConcurrencyToken()` (no migration required: for a plain `int` token the config is model-metadata only — `dotnet ef migrations has-pending-model-changes -c TenantDbContext` reports none). All five mutating repository seams (`UpdateAsync`/`SetStatusAsync`/`SetRanksAsync`/`SetParentAsync`/`RekeyAsync`) already bump `Version` and now translate `DbUpdateConcurrencyException` into the typed, **retryable** `TRACKER.CONCURRENCY_CONFLICT`. Real-Postgres proof: `WorkItemRepositoryTests.Interleaved_rekeys_conflict_typed_instead_of_silently_losing_history` establishes a deterministic interleave (both rekeys read at `Version=1`, queued behind an external `FOR UPDATE` on the project row) and shows the loser gets the typed conflict while the winner's `PreviousKeys` chain stays intact — no more silent last-write-wins losing key history.

Still open: none. Both items below closed on 2026-07-30 — see the next section.

## Sweep-endpoint hygiene — resolved 2026-07-30

All four items of the "sweep endpoint hygiene" follow-up shipped, plus the endpoint's
missing HTTP-authorization proof. New/changed code:
`Tamma.Api/Endpoints/Admin/AdminTenantMigrationEndpoints.cs` (new — the handler moved out of
the `Program.cs` lambda), `Tamma.Api/Dtos/Admin/AdminTenantMigrationDtos.cs` (new),
`Tamma.Data/Abstractions/ITenantMigrationSweepRunner.cs` (new),
`Tamma.Data/Pooling/TenantMigrationSweepRunner.cs` (new),
`Tamma.Data/Pooling/EfTenantDbMigrator.cs`, `Tamma.Data/DependencyInjection.cs`,
`Tamma.Api/Program.cs` (route mapping only). `TenantMigrationSweeper` itself is unchanged —
the hygiene is a wrapper, not a redesign, exactly as the sweep was a caller and not a redesign.

### ⚠️ BREAKING — the endpoint's default flipped from APPLY to DRY RUN

`POST /api/admin/tenants/migrate` used to be `dryRun ?? false`: a bare POST with no body and no
query **applied schema migrations to every provisioned tenant**. An operator poking the endpoint
to see what it does mutated the whole fleet. The safe action is now the default. The new contract:

| Request | Behaviour |
| --- | --- |
| `POST .../migrate` (bare) | **Dry run.** `200` + per-tenant pending counts, `applied=false`, nothing written. |
| `POST .../migrate?dryRun=true` | Same (explicit spelling; unchanged from before). |
| `POST .../migrate?async=true` | Dry run as a background run: `202` + run id (for a fleet big enough that even the pending-count walk is slow). |
| `POST .../migrate?apply=true` **+** `X-Admin-Confirm: migrate-all-tenants` | The real sweep. `202 Accepted` + run id + status URL. |
| `POST .../migrate?apply=true` without the header | `400 confirmation_required`. |
| `POST .../migrate?dryRun=false` | `400 apply_requires_explicit_opt_in` — the OLD spelling for "apply" is refused **loudly**. Anyone already scripted against the old default learns from an error, not from a fleet that migrated when they expected a report (and not from a silent no-op either). |
| `POST .../migrate?apply=true&dryRun=true` | `400 conflicting_mode`. |
| A second apply while one runs | `409 sweep_already_running` (see item 2). |
| `GET .../migrate/{runId}` | Run status; `404 run_not_found_on_this_instance` if this instance never had it. |

**Why `?apply=true` and not `?dryRun=false`:** the opt-in to a destructive action reads as an
affirmative, never as a double negative. The paired confirmation header follows the neighbouring
admin surface — `force-delete` and `cleanup` (`AdminTenantsEndpoints.cs:556`, `:637`) both demand
`X-Admin-Confirm` echoing the tenant id. A sweep has no single tenant id to echo, so the constant
`migrate-all-tenants` plays that role; it is not typeable by accident. Every response carries both
`mode` (`dry-run` | `apply`) and `applied` so which mode ran is never inferred from counts; the
200 body is otherwise **flat and field-compatible** with the old result (`dryRun`, `total`,
`migrated`, `alreadyCurrent`, `pending`, `failed`, `tenants` stay top-level).

### 1. `dryRun` default — done

Flipped as above. Mode resolution rejects every ambiguous combination rather than picking a
winner. Tests: `TenantMigrationEndpointAuthTests.BarePost_IsADryRun_AndSaysSoUnmistakably`,
`.DryRunFalse_IsRefused_LoudlyRatherThanReinterpreted`, `.ApplyAndDryRunTogether_Is400`,
`.Apply_WithoutTheConfirmHeader_Is400`.

The same defect one layer down is closed too: `ITenantMigrationSweeper.SweepAsync`'s `dryRun`
parameter **lost its `= false` default**, so `SweepAsync()` — the shortest thing an in-code caller
can write — no longer means "apply DDL to every tenant". Every call site now states the mode
(compiler-enforced; the existing sweeper tests were updated to `SweepAsync(dryRun: false)` where
they were already testing the apply path).

### 2. Single-flight guard — done, CLUSTER-wide

Two concurrent applies used to both sweep, double-migrating every tenant (EF's per-migration
transaction makes the loser mostly record failures — noise and wasted fleet-wide load at best).
The guard is **cluster-wide**, not per-process: on a multi-pod deploy the two racing POSTs are
exactly as likely to land on two different pods, where a `SemaphoreSlim` is decoration. It is a
Postgres **session-scoped `pg_try_advisory_lock`** on a dedicated control-plane connection —
the `HourlyAnalyticsRollupScheduler` / `ScheduleLockKey` idiom, with the same `pg_locks`-greppable
ASCII namespace convention (`"MGSW"`, key `0x4D47535700000001`; no partition component because
there is exactly one sweep gate for the whole cluster). Session scope means a crashed pod's lock
dies with its connection — the gate cannot wedge shut, which is the property a fleet-DDL escape
hatch must have.

A process-local slot is taken *first*, purely so the 409 can be exact: `scope=this-instance` with
the running sweep's `runId` and `startedAt`, versus `scope=another-instance` with both null and a
message saying so (this process genuinely cannot know a remote run's identity, and inventing one
would be fiction). It is also the whole guard on a non-Postgres provider (test hosts).

**Only apply sweeps take the lock.** A dry run writes nothing, so it cannot double-migrate
anything, and refusing "what is still pending?" while a long apply runs would remove the one
question an operator most wants answered mid-run.

Tests (`TenantMigrationSweepRunnerTests`, real Postgres):
`Second_apply_sweep_on_the_same_instance_is_refused_with_the_running_runs_identity`,
`Second_apply_sweep_from_another_instance_is_refused_by_the_cluster_lock` (two runner instances
over one database = two pods — the case a per-process lock misses),
`The_gate_reopens_after_the_run_completes`,
`A_sweep_that_throws_fails_the_run_and_still_releases_the_gate`,
`Dry_runs_are_not_gated_by_the_apply_single_flight`,
`IsSweepRunning_sees_a_sweep_held_by_another_instance_and_clears_afterwards`.

### 3. Synchronous in-request execution — done, 202 + poll

Apply now returns `202 Accepted` with a `runId` and a `statusUrl`, mirroring the existing
`POST /api/admin/tenants/{id}/provision` + `GET .../provisioning` and
`POST /api/admin/tenants/{id}/move` + `GET .../move` shape.

**Only the wire shape is borrowed, not the mechanism.** The move endpoint enqueues a
`PlatformQueuedTask`; a sweep must not, because `PlatformTaskWorker` ships `RunOnStartup=false`
(CLAUDE.md, "Known constraint"), so a queued sweep would sit un-drained in the default deployment
— the endpoint would silently do nothing, strictly worse than the synchronous version it replaces.
The run therefore executes on an in-process background task tied to process shutdown (explicitly
**not** the request's `CancellationToken`, which is canceled the moment the 202 is written).

**Dry run stays synchronous (200).** It does no DDL: per tenant it is one pooled connection and
one `__TenantMigrationsHistory` read, 4-way parallel. The unbounded cost item 3 is about is the
migration DDL itself, which only the apply path runs — and the dry run is now the *default*, so
making an operator poll twice to learn "nothing is pending" would be a worse surface. `?async=true`
gives a dry run the same 202 treatment for a fleet large enough that even that walk is slow.

**Known limitation, deliberate:** run state is in-memory and per-instance. A poll that
load-balances onto another pod gets `404 run_not_found_on_this_instance` plus
`sweepRunningOnSomeInstance` (a read-only `pg_locks` probe — acquiring-then-releasing would let a
status poll perturb the gate it is reporting on). Durable cluster-visible run rows would need a
control-plane table; not built, because the lock already prevents the damage and the operator's
fallback (poll the accepting instance, or re-POST and read the 409) is honest. Tests:
`Start_returns_before_the_sweep_finishes_and_the_result_arrives_by_polling`,
`TenantMigrationEndpointAuthTests.Apply_WithConfirmHeader_Is202_AndTheRunIsPollableToCompletion`,
`.DryRun_CanOptIntoTheSame202_ForAVeryLargeFleet`,
`.UnknownRunId_Is404_AndSaysRunStateIsPerInstance`.

### 4. `CommandTimeout=30s` on migration DDL — done

The tenant pool stamps `CommandTimeout=30` onto every tenant connection string
(`TenantConnectionPoolOptions.CommandTimeoutSeconds`, applied in
`LruPooledTenantConnectionResolver.BuildDataSource`), and the sweep migrates over connections
borrowed from that pool — so the CHECK-widening this story predicts on the highest-row-count table
would abort at 30s and land as a per-tenant `failed` row indistinguishable from a real breakage.

`EfTenantDbMigrator.MigrationCommandTimeoutSeconds = 900` (15 min) is now set **at the EF layer on
the migration context's options only** (`BuildConnectionOptions` and `BuildStringOptions` — the
provisioning flavour gets it too; a slow baseline on a fresh schema exceeds 30s just as easily).
The pool, the connection strings and `TenantDbContextFactory` are untouched, so every runtime
context over the same data source still inherits the 30s ceiling. EF migrations stay transactional
per migration, so a genuine timeout still rolls that migration back — the longer ceiling removes
spurious failures, it does not create partially-applied schemas.

Both halves pinned by `EfTenantDbMigratorCommandTimeoutTests`:
`Migration_over_a_borrowed_connection_uses_the_long_DDL_timeout`,
`Migration_over_a_connection_string_uses_the_long_DDL_timeout`, and
`The_runtime_tenant_context_still_gets_the_pools_thirty_second_timeout` (the runtime factory sets
no EF-level timeout and the pool default is still 30 — a "fix" that raised the pool's timeout
would silently give every request-path query a 15-minute ceiling).

### 5. The missing HTTP-authorization test — closed (stale entry)

The "no HTTP-level authorization test" item was already stale: `Tamma.Api.Tests/Tracker/
TenantMigrationEndpointAuthTests.cs` drives the real (non-permissive) bearer-JWT pipeline and
asserts 401 unauthenticated / 403 for member, tenant-admin and tenant-owner / 200 for
`platformRole=platform_admin`. This lane extended it with `Member_Gets403_OnRunStatus` (the new
`GET .../migrate/{runId}` route carries the same `PlatformOwnerAccess` gate) and the contract
tests above.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
| 2026-07-29 | 1.0.1   | Adversarial-review follow-ups section (fixes landed in the tracker-storage lane; deferred items listed) | Claude |
| 2026-07-29 | 1.0.2   | `Version` concurrency-token follow-up closed: `IsConcurrencyToken()` + typed retryable `TRACKER.CONCURRENCY_CONFLICT` on all update seams + interleaved-rekey Postgres proof (no migration needed) | Claude |
| 2026-07-30 | 1.1.0   | **Sweep-endpoint hygiene closed (BREAKING: `POST /api/admin/tenants/migrate` now defaults to a DRY RUN; applying needs `?apply=true` + `X-Admin-Confirm: migrate-all-tenants`)** — plus cluster-wide `pg_try_advisory_lock` single-flight with a typed 409, 202-plus-poll background execution, and a 15-minute migration-DDL command timeout that leaves the runtime pool's 30s untouched | Claude |
