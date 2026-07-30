# Implementation Plan — Story 44-1: Storage, Repositories, and the Migrate-All-Provisioned-Tenants Sweep

> **RE-CUT 2026-07-29** against the current story file and the SHIPPED `Tamma.Core.Tracking`
> (PR #506, all 11 files read). Supersedes the pre-44-0-rework plan, which missed the four
> things the rework handed this story: **`SiblingRank`** (second `COLLATE "C"` rank column,
> 44-0 AC10), **`PreviousKeys`** (frozen-key history, 44-0 AC8 / `WorkItemKeyHistory`),
> **`Estimate` + `EstimateScale`** (scale-free `decimal?` on the item, scale on the project,
> 44-0 AC13), and the **`work_item_relations`** table (44-0 AC14 — rows stored in the form
> `WorkItemRelationKindExtensions.Canonicalize` returns: symmetric kinds lower-id-first;
> `blocks` kept directed; the shipped helper is CALLED, never reimplemented).

## Scope & Deliverable

When this story is done, **five** tables (`projects`, `work_items`, `work_item_relations`,
`iterations`, `tracker_preferences`) exist in every tenant schema — including tenants
provisioned before the deploy, because this story also builds the migrate-all-provisioned-tenants
sweep the platform has never had. Both `work_items."Rank"` and `work_items."SiblingRank"` are
`COLLATE "C"` so Postgres `ORDER BY` and `StringComparer.Ordinal` agree (the storage obligation
`Rank.cs` states in its own doc); `ck_work_items_status` / `ck_work_items_kind` mirror 44-0's
wire sets (8 and 4) and tests prove set equality by reflection; keys are minted
`(ProjectId, Number)`-unique under a `FOR UPDATE` row lock; a re-key appends the outgoing key to
`PreviousKeys` via `WorkItemKeyHistory.Record` and lookup-by-key resolves current-or-previous.
Repositories exist for all of it, with parallel never-joined mode surfaces on
`tracker_preferences` only.

## Pre-Reading (all verified present)

- `docs/stories/epic-44/README.md` — §5 (residency + the migration gap), §6 (ownership per mode;
  why the XOR does NOT apply to work items), D5/D6/D7/D12/D13
- `apps/tamma-elsa/src/Tamma.Core/Tracking/` — ALL 11 files. Load-bearing for this story:
  `Rank.cs` (collation obligation stated at `:14-22`; `Append`/`Prepend` are digit-carry, not
  midpoint — the pre-44-1 convention note at `:44-59`), `WorkItemRef.cs` (frozen key,
  `IsValidProjectKey`, strict parse), `WorkItemKeyHistory.cs` (`Record`/`Matches` — the
  current-or-previous rule, one implementation), `WorkItemRelationKind.cs`
  (`Canonicalize` at `:92-109` — THE direction convention; `IsSymmetric`), `EstimateScale.cs`
  (project-level scale; `AllowsEstimate`), `WorkItemStatus.cs` / `WorkItemKind.cs` (wire sets
  the CHECKs mirror), `TrackerPriority.cs` (priority nullable; wires = `TriagePriority`'s)
- `Tamma.Data/Pooling/EfTenantDbMigrator.cs` — schema from `Search Path` (`:48`),
  `Pooling=false` (`:58-62`), `__TenantMigrationsHistory` pinned per schema (`:64-67`),
  `CREATE SCHEMA` DO-block safety net (`:88-93`), idempotent `MigrateAsync` (`:97`)
- `Tamma.Api/Services/Provisioning/TenantProvisioningService.cs:147-188` — creation-only call
  site #1 (`password is not null` gate at `:169`); `Tamma.Activities/TenantLifecycle/
  MigrateTenantDatabaseActivity.cs:52` — call site #2
- `Tamma.Data/Abstractions/ITenantConnectionResolver.cs` — `GetDataSourceAsync`; the resolver
  decrypts `tenants.EncryptedConnectionString`, so the sweep CAN reach a tenant the
  provisioning re-run path cannot (no plaintext creds needed — the data source carries them)
- `Tamma.Data/Migrations/Tenant/20260722011909_AddAcceptanceRulesOverrides.cs` — strong XOR
  (`:32`) + `NullsDistinct=false` unique index (`:35-40`); `20260722180002_AddDocumentInstances.cs`
  — CHECK-mirrors-enum precedent (`:39`); `20260704083332_AddDomainEventsUserIdIndex.cs` — the
  raw-SQL `IF NOT EXISTS` pattern for indexes the EF model cannot express
- `Tamma.Data/Repositories/IAcceptanceRulesRepository.cs` + `AcceptanceRulesRepository.cs` —
  the parallel-surface contract and predicate style (`p.UserId == userId && p.TenantId ==
  default(Guid?)`), and the `ITenantDbContextFactory` + ambient `ITenantContext` shape
- `tests/Tamma.Api.Tests/AcceptanceRules/AcceptanceRulesOverridesMigrationTests.cs` +
  `tests/Tamma.Api.Tests/Tenancy/SchemaPerTenantMigrationTests.cs` — the Testcontainers
  migration-test shapes (single-schema and two-schema); Story 39-11's `AddDocumentInstances`
  is the freshest table-adding tenant-migration precedent (EF-generated, house shape)
- `Tamma.Api/Program.cs:3293-3335` — the Epic 19 destructive startup wipe. It enumerates
  **public-schema CP-era tables by name**; tenant-schema tables (`document_instances`,
  `acceptance_rules_overrides`, `channel_outbox`) are deliberately absent. The tracker tables
  follow the same rule: **they are NOT added to the DROP list** (tenant-resident, reached only
  through per-schema migrations — nothing to wipe in `public`).

## Design Decisions

- **D1 — Tenant-schema residency; the sweep is the price of admission.** (Unchanged from v1,
  epic D5.) `EfTenantDbMigrator` is already idempotent, schema-pinned and history-tabled; the
  sweep is a caller over `tenants` × `ITenantConnectionResolver`, not a redesign.
- **D2 — The sweep core lives in `Tamma.Data` (`TenantMigrationSweeper`); the HTTP mapping is a
  thin admin route.** This round the `Program.cs` mapping is owned by another lane (41-30), so
  44-1 ships `ITenantMigrationSweeper` + implementation + DI + Testcontainers proof, and the
  `POST /api/admin/tenants/migrate` route is a deferred one-liner recorded in the handoff.
  Explicit admin action, never boot-time; per-tenant failure isolation; bounded concurrency
  (default 4 — each migration takes a non-pooled physical connection); `dryRun` reports pending
  counts without applying.
- **D3 — BOTH rank columns are `text COLLATE "C"`** — `Rank` (flat backlog) and `SiblingRank`
  (order under the parent, null parent included). One algebra, two columns (44-0 AC10). EF 8's
  `UseCollation("C")` expresses this in the model, so the migration and snapshot carry it
  natively (verify the generated `Up()` says `collation: "C"`; hand-fix if the provider drops
  it). The Testcontainers test inserts real `Rank.Between`/`Append`/`Prepend` output shuffled
  and asserts `ORDER BY` ≡ `StringComparer.Ordinal` for both columns.
- **D4 — Five tables in ONE migration (`AddTrackerCore`)**, including `iterations` (populated
  by 44-4) and `work_item_relations` (validated by 44-3). Tenant migrations are the scarcest
  resource in the repo; the sweep makes them survivable, not free.
- **D5 — No principal XOR on `projects`/`work_items`/`work_item_relations`/`iterations`; the
  strong XOR + `NullsDistinct=false` unique index on `tracker_preferences` only.** Work items
  are content (epic D6); `tracker_preferences` is genuine per-principal configuration and takes
  the `acceptance_rules_overrides` pattern exactly.
- **D6 — Key minting is `projects."NextNumber"` under `FOR UPDATE`**, in the create
  transaction. Gap-free, monotone; `(ProjectId, Number)` unique + `Key` unique are the
  belt-and-braces; a 50-way concurrency test proves contiguity. The minted string is
  `new WorkItemRef(project.Key, number).ToWire()` — never string-interpolated by hand.
- **D7 — `PreviousKeys` is `text[] NOT NULL DEFAULT '{}'`**, written ONLY through
  `WorkItemKeyHistory.Record` (idempotent, order-preserving) on the explicit re-key seam
  (`RekeyAsync`). A project MOVE writes nothing (the key is frozen — 44-0 AC8/D13).
  `GetByKeyAsync` resolves `Key == k OR PreviousKeys @> {k}`; a GIN index on `PreviousKeys`
  serves the array containment.
- **D8 — `work_item_relations` stores rows in Canonicalize'd form and the unique index assumes
  it.** `(SourceId, TargetId, Kind)` unique; the repository's `AddRelationAsync` calls
  `WorkItemRelationKindExtensions.Canonicalize(kind, source, target)` — the single
  implementation of "symmetric ⇒ lower id first, `blocks` ⇒ meaning-preserving" — so a mirror
  duplicate of a symmetric edge maps onto the same stored row and hits the unique index.
  A `ck_work_item_relations_no_self` CHECK (`SourceId <> TargetId`) backs `Canonicalize`'s
  `TRACKER.SELF_RELATION` at the DB layer; everything further (cross-project, cycles-are-shown)
  is 44-3's. FKs to `work_items` cascade — an item's edges die with it.
- **D9 — `Estimate` is `numeric NULL` on the work item; `EstimateScale` is a wire string on the
  project** (default `not_used`, CHECK over the 5 wires). NOT `EstimateHours`. Coherence
  (`AllowsEstimate`) is 44-2's API-boundary rule; storage stays permissive per 44-0 AC13.
- **D10 — `ExternalRefJson` is one `jsonb` column** (44-8 owns the shape). The already-linked
  skip index is a raw-SQL partial expression index (`IF NOT EXISTS`, the
  `AddDomainEventsUserIdIndex` pattern) because EF cannot express it.
- **D11 — `projects.RepositoryId` is a bare `Guid?`, no FK, no local `repositories` table**
  (39-20 owns the registry; epic boundary table).
- **D12 — `Priority` and `IssueType` are nullable wire strings** with CHECKs permitting NULL.
  `null` priority = "nobody prioritised" (44-0 AC11). `IssueType` is nullable for the same
  triage-queue reason (an imported/triaged item exists before anyone classified it;
  `TriageIssueType` has no "unset" member) — a deliberate small extension of AC12's binding,
  recorded here; 44-2 may tighten at the API boundary.
- **D13 — Repositories in `Tamma.Data/Repositories/`, DI in `Tamma.Data/DependencyInjection.cs`**
  beside `IAcceptanceRulesRepository` — never in `Program.cs`. `Version` is an int bumped on
  write (`AcceptanceRulesOverride.Version` precedent; 44-2's ETag).
- **D14 — No entry in the Epic 19 startup DROP list.** The wipe enumerates public-schema tables
  by name; tracker tables are tenant-schema-resident and must never appear there (matching
  `document_instances` / `acceptance_rules_overrides` / `channel_outbox`).

## Implementation Steps

1. **CREATE `Tamma.Data/Entities/ProjectEntity.cs`** — `Id`, `Key`, `Name`, `Description`,
   `RepositoryId` (`Guid?`, D11), `EstimateScale` (wire string, default `not_used`, D9),
   `NextNumber` (D6), `ArchivedAt`, `CreatedByUserId`, `CreatedAt`, `UpdatedAt`, `Version`.
2. **CREATE `Tamma.Data/Entities/WorkItemEntity.cs`** — story AC1 column set verbatim: `Id`,
   `ProjectId`, `Key` (frozen), `PreviousKeys` (`text[]`, default `{}`), `Number`, `Kind`,
   `Status`, `Priority` (nullable), `IssueType` (nullable, D12), `Title`, `Description`,
   `ParentId`, `IterationId`, `Rank`, `SiblingRank`, `AssigneeUserId`, `CreatedByUserId`,
   `Estimate` (`decimal?`), `ExternalRefJson`, `CreatedAt`, `UpdatedAt`, `ClosedAt`, `Version`.
   Kind/Status/Priority/IssueType stored as **wire strings** (CHECKs enforce; Core parses).
3. **CREATE `Tamma.Data/Entities/WorkItemRelation.cs`** — `Id`, `SourceId`, `TargetId`, `Kind`
   (wire string), `CreatedByUserId`, `CreatedAt`. Doc comment states the canonical-form
   invariant and points at `Canonicalize`.
4. **CREATE `Tamma.Data/Entities/IterationEntity.cs`** — `Id`, `ProjectId`, `Name`, `StartsOn`,
   `EndsOn`, `Status` (`planned|active|closed`), `CapacityPoints` (`decimal?`), timestamps,
   `Version`. Populated by 44-4.
5. **CREATE `Tamma.Data/Entities/TrackerPreference.cs`** — `Id`, `UserId?`, `TenantId?`,
   `DefaultProjectId`, `DefaultKind`, `BoardGroupBy`, `CreatedBy`, `UpdatedBy`, timestamps,
   `Version`. Dual-scoping doc mirrors `AcceptanceRulesOverride.cs`.
6. **MODIFY `TenantDbContext.cs`** — five `DbSet`s in the tenant block.
7. **MODIFY `TammaModelConfiguration.cs`** — one contiguous `ConfigureTrackerEntities` region
   (five `ToTable` blocks: CHECKs `ck_projects_estimate_scale`, `ck_work_items_status` (8),
   `ck_work_items_kind` (4), `ck_work_items_priority`, `ck_work_items_issue_type`,
   `ck_work_item_relations_kind` (3), `ck_work_item_relations_no_self`, `ck_iterations_status`,
   `ck_tracker_preferences_principal_xor` (strong), `ck_tracker_preferences_default_kind`;
   `UseCollation("C")` on both rank columns; unique/GIN/covering indexes) + ONE call line at
   the end of `ConfigureTenantEntities`.
8. **CREATE the migration** `Migrations/Tenant/<ts>_AddTrackerCore` via
   `dotnet ef migrations add AddTrackerCore --context TenantDbContext --output-dir Migrations/Tenant`.
   Verify `collation: "C"` on both rank columns in the generated `Up()`; append the raw-SQL
   `IF NOT EXISTS` partial expression index on `ExternalRefJson` (D10). Run
   `dotnet ef migrations has-pending-model-changes` for both contexts — clean.
9. **CREATE repositories** — `IProjectRepository`/`ProjectRepository`,
   `IIterationRepository`/`IterationRepository`,
   `IWorkItemRepository`/`WorkItemRepository` (get / get-by-key-current-or-previous /
   list+keyset-page ordered `(Rank, Id)` / create-with-minting / update / set-status (stamps
   `ClosedAt` from `IsTerminal()`) / set-ranks / set-parent / rekey (via
   `WorkItemKeyHistory.Record`) / delete / relation add–remove–list via `Canonicalize`),
   `ITrackerPreferenceRepository`/`TrackerPreferenceRepository` (the six paired parallel
   methods, AC6). All via `ITenantDbContextFactory` + ambient `ITenantContext`.
10. **CREATE `Tamma.Data/Abstractions/ITenantMigrationSweeper.cs` +
    `Tamma.Data/Pooling/TenantMigrationSweeper.cs`** — `SweepAsync(dryRun, maxConcurrency, ct)`
    → enumerate non-deleted `tenants` via `IDbContextFactory<ControlPlaneDbContext>`, resolve
    each data source via `ITenantConnectionResolver`, then probe/apply THROUGH the data source.
    **Found while applying (Testcontainers proof):** `NpgsqlDataSource.ConnectionString` strips
    the password (SASL/SCRAM "No password has been provided"), so the sweep cannot round-trip
    a resolved data source into the string-based `ITenantDbMigrator`. `EfTenantDbMigrator`
    therefore gains a data-source seam — `ITenantDataSourceDbMigrator`
    (`MigrateTenantAppAsync(NpgsqlDataSource)` + `CountPendingMigrationsAsync`), one shared
    core, schema still derived from the data source's `Search Path` (which survives the
    stripping); `ITenantDbMigrator` and its test stubs are unchanged. Per-tenant try/catch →
    `migrated` / `already-current` / `pending` / `failed` rows; `SemaphoreSlim` bound.
11. **MODIFY `Tamma.Data/DependencyInjection.cs`** — repository registrations after `:160` +
    `TryAddSingleton<ITenantDbMigrator, EfTenantDbMigrator>` + the sweeper.
12. **DEFERRED (out of lane, exact edits recorded in the handoff):** the
    `POST /api/admin/tenants/migrate` route mapping (`PlatformOwnerAccess`) in `Program.cs` and
    its endpoint file — the sweep service + tests land here; the HTTP skin is a MapPost over
    `ITenantMigrationSweeper`.

## Data & Migrations

One tenant migration, `AddTrackerCore`:

| Table | Notable |
|---|---|
| `projects` | `Key` unique; `NextNumber` (D6); `EstimateScale` + CHECK (D9); `RepositoryId Guid?` unenforced (D11) |
| `work_items` | `Rank` **and** `SiblingRank` `text COLLATE "C"` (D3); `PreviousKeys text[] DEFAULT '{}'` + GIN (D7); `(ProjectId, Number)` unique; `Key` unique; `ParentId` self-FK RESTRICT; `IterationId` FK SET NULL; `ProjectId` FK RESTRICT; CHECKs status/kind/priority/issue-type; indexes `(ProjectId, Status, Rank)`, `(ProjectId, ParentId, SiblingRank)`, `(AssigneeUserId, Status)`, `(IterationId)`; raw-SQL partial index on `ExternalRefJson` keys (D10) |
| `work_item_relations` | unique `(SourceId, TargetId, Kind)` **assuming canonical form** (D8); CHECKs kind + no-self; FKs CASCADE; index `(TargetId)` for reverse lookups |
| `iterations` | `(ProjectId, Name)` unique; `ck_iterations_status`; FK CASCADE |
| `tracker_preferences` | strong XOR; unique `(UserId, TenantId)` `NullsDistinct=false`; `ck_tracker_preferences_default_kind` |

`ParentId` is RESTRICT (deleting an epic must not silently delete the subtree — 44-2 returns
409). **No control-plane migration. No change to `domain_events`. No DROP-list entry (D14).**

## Events

None — 44-5. The sweep emits no DCB event (platform DDL, no tenant stream to append into; the
result list is the record).

## Test Plan

| # | Test | Kind |
|---|---|---|
| 1 | `TrackerMigrationTests.All_five_tables_land_in_the_tenant_schema` | Testcontainers |
| 2 | `...Status_check_constraint_matches_the_enum` (reflect `WorkItemStatus` wires ↔ `pg_constraint`) | Testcontainers |
| 3 | `...Kind_check_constraint_matches_the_enum` | Testcontainers |
| 4 | `...Rank_and_SiblingRank_are_C_collated_and_sort_ordinally` (real `Rank` output, shuffled, both columns) — **AC4** | Testcontainers |
| 5 | `...Preferences_xor_rejects_both_null_and_both_set` + `...unique_index_dedupes_the_null_half` | Testcontainers |
| 6 | `...Relations_unique_index_rejects_duplicate_canonical_row` + no-self CHECK | Testcontainers |
| 7 | `WorkItemRepositoryTests.Concurrent_creates_mint_distinct_contiguous_keys` — **AC5** | Testcontainers |
| 8 | `...GetByKey_resolves_previous_key_after_rekey` (frozen-key + `PreviousKeys` history) | Testcontainers |
| 9 | `...Mirror_symmetric_relation_is_rejected_and_blocks_stays_directed` (via `Canonicalize`) | Testcontainers |
| 10 | `...Keyset_paging_is_stable_under_insertion`; `...Parent_delete_is_restricted` | Testcontainers |
| 11 | `TrackerPreferenceRepositoryTests.Planes_never_join` | Testcontainers |
| 12 | `TrackerOwnershipTests.No_work_item_surface_filters_on_a_principal_plane` (reflection — pins D5) | plain NUnit |
| 13 | `TenantMigrationSweeperTests.Sweep_reaches_a_pre_provisioned_tenant` (two schemas; one migrated by the creation path; sweep; other gains tables; first `already-current`) — **AC9** | Testcontainers |
| 14 | `...Sweep_is_idempotent`; `...One_failing_tenant_does_not_abort`; `...DryRun_applies_nothing` | Testcontainers |

Endpoint 403 test (platform-owner-only) ships with the deferred route mapping.

## Dependencies & Sequencing

- **Blocked by:** 44-0 (**shipped**, PR #506).
- **Blocks:** 44-2, 44-3, 44-4, 44-5, 44-7, 44-8, 44-9.
- **Shared-edit register:** `TenantDbContext.cs`, `TammaModelConfiguration.cs` (one contiguous
  region), `TenantDbContextModelSnapshot.cs` (single-author token), `Tamma.Data/DependencyInjection.cs`.
  `Program.cs` explicitly NOT touched this round (41-30's lane) — handoff carries the exact edits.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Collation silently dropped on regenerate | `UseCollation("C")` is in the MODEL (snapshot carries it); test 4 fails loudly against real Postgres |
| A caller reimplements the direction convention | repository is the only writer of `work_item_relations` and calls `Canonicalize`; test 9 pins mirror rejection |
| Sweep = new way to break production | dryRun; per-tenant isolation (test 14); bounded concurrency; runs only code every tenant-create already runs |
| Key minting serializes bulk imports | acceptable v1; noted for 44-8/44-9 to batch under one lock if needed |

## Effort

Original 6.0d figure stale (banner). Re-cut: entities/config/migration 2.0, repositories 1.5,
sweeper 0.5, tests 1.5, docs/handoff 0.5 → **6.0 days** (net unchanged — relations + second
rank column + history offset by the endpoint skin moving out of lane).

## Post-landing amendment — 2026-07-30 sweep-runner adversarial review

Six findings against the sweep-hygiene commit (`d1e9362`) are closed; full write-up in the story
file's "Sweep-runner adversarial review — fixed 2026-07-30" section. The two that change what a
reader of this plan would otherwise assume:

- **New deployment requirement.** The cluster single-flight gate is a *session-scoped*
  `pg_try_advisory_lock`, so `ConnectionStrings:ControlPlane` **must not** sit behind a
  transaction-mode connection pooler (PgBouncer `pool_mode = transaction` and equivalents). There
  the lock is taken on one backend and released on another — silently ineffective while appearing to
  work. Direct connection or `pool_mode = session` only.
- **Wire change.** `applied` on every `/api/admin/tenants/migrate` response is now a tri-state
  string (`not-applied` / `partially-applied` / `applied`), not a boolean, and a failed run reports
  the partial per-tenant result (`resultIsPartial: true`) rather than `null`.

Also: the advisory lock is re-verified every 15s from its own session and the run aborts on loss;
the dry run's pending-count read no longer inherits the 900s DDL ceiling on the endpoint's
synchronous default path; background dry runs are admission-capped at 4 per instance
(`429 dry_run_capacity_exhausted`); `Dispose` no longer disposes the shutdown source under a running
sweep; and the `pg_locks` probe is qualified by `objsubid` and database oid.

Seam changes: `ITenantMigrationSweeper.SweepAsync` gained an `Action<TenantMigrationSweepEntry>?
onTenantCompleted` parameter **before** `ct`; `TenantMigrationSweepRun` gained `ResultIsPartial`;
`TenantMigrationSweepConflict` gained `ScopeDryRunCapacity`; `TenantMigrationSweep` gained
`Summarize`.
