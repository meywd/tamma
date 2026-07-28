# Implementation Plan — Story 44-1: Storage, Repositories, and the Migrate-All-Provisioned-Tenants Sweep

> **⚠️ PREDATES THE 44-0 REWORK (conformance review, 2026-07-28).** This plan was written before
> the v2 rework of 44-0 and before 44-0 shipped. The STORY file is current; this plan is not — it
> never mentions four things the story and the shipped `Tamma.Core.Tracking` now require 44-1 to
> store: **`SiblingRank`** (second `COLLATE "C"` rank column), **`PreviousKeys`** (key history),
> **`Estimate` + `EstimateScale`**, and the **`work_item_relations`** table (rows stored in
> `Canonicalize`d lower-id-first form; validation is 44-3's). Re-cut this plan against the story
> file and `Tamma.Core/Tracking/` before starting; treat effort figures as stale.


## Scope & Deliverable

When this story is done, four tables (`projects`, `work_items`, `iterations`, `tracker_preferences`) exist in every tenant schema — including tenants that were provisioned before the deploy, because this story also builds the `POST /api/admin/tenants/migrate` sweep the platform has never had. `work_items."Rank"` is `COLLATE "C"` so the database and the API agree on order; `ck_work_items_status` / `ck_work_items_kind` mirror 44-0's wire strings and a test proves it; keys are minted `(ProjectId, Number)`-unique under a row lock and a concurrency test proves that. Repositories exist for all four, with parallel never-joined mode surfaces on `tracker_preferences` only — work items are content, not per-principal configuration, and a test asserts no work-item query filters on a user ownership plane.

## Pre-Reading

- `docs/stories/epic-44/README.md` — §5 (residency + the migration gap), §6 (ownership per mode, and why the XOR does not apply to work items), Decisions D5/D6/D7
- `docs/stories/epic-44/story-44-0/implementation-plan.md` — D6 (`WorkItemRef` format is load-bearing), D7 (the `COLLATE "C"` obligation this story discharges)
- `apps/tamma-elsa/src/Tamma.Data/Pooling/EfTenantDbMigrator.cs:25-100` — the whole migrator; note `:47` schema derivation, `:56-62` `Pooling=false`, `:64-67` history table, `:83-92` `CREATE SCHEMA IF NOT EXISTS`, `:97` `MigrateAsync`
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantProvisioningService.cs:147-176` — call site #1 and the `password is not null` skip that makes it creation-only
- `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/MigrateTenantDatabaseActivity.cs:53` + `Tamma.ElsaServer/Workflows/CreateTenantWorkflow.cs:158-160` — call site #2
- `apps/tamma-elsa/src/Tamma.Data/Pooling/LruPooledTenantConnectionResolver.cs:233,340,495` — resolve / lease / evict
- `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/20260722011909_AddAcceptanceRulesOverrides.cs:14-40` — table + **strong** XOR (`:32`) + `NullsDistinct=false` unique index (`:35-40`)
- `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/20260722180002_AddDocumentInstances.cs` — the freshest tenant migration; `ck_document_instances_status` is the CHECK-mirrors-enum precedent
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IAcceptanceRulesRepository.cs:5-41` + `AcceptanceRulesRepository.cs:17-140` — the parallel-surface contract and its predicate style
- `apps/tamma-elsa/tests/Tamma.Api.Tests/AcceptanceRules/AcceptanceRulesOverridesMigrationTests.cs:49` + `tests/Tamma.Api.Tests/Tenancy/SchemaPerTenantMigrationTests.cs:82-86` — the Testcontainers migration-test shape
- `docs/stories/epic-39/story-39-20/implementation-plan.md:57-60` — 39-20's `repositories` table, which AC10 defers to
- **All referenced paths exist.** NOT FOUND (this story creates them): the four entities, `Migrations/Tenant/*_AddTrackerCore.*`, the four repositories, `Endpoints/Admin/AdminTenantMigrationEndpoints.cs`.

## Design Decisions

- **D1 — Tenant-schema residency, and the sweep is this story's price of admission.** Epic 43 put `action_assignments` in the control plane partly *because* a new tenant migration never reaches existing tenants (`epic-43/README.md:238-246`). That escape is not available here: work items are the highest-row-count operational data in the system and CP residency would put every tenant's backlog in one table, forfeiting the isolation `SchemaPerTenantMigrationTests.cs:82-86` exists to prove. So the gap gets fixed instead of avoided. `EfTenantDbMigrator` is already idempotent, already history-tabled per tenant, already schema-safe — the sweep is a caller over `tenants` × `LruPooledTenantConnectionResolver`, not a redesign.

- **D2 — The sweep is an explicit admin action, never automatic on startup.** An automatic boot-time sweep over N tenants serializes deploy on N migrations and turns one bad migration into a total outage. `PlatformTaskWorker.RunOnStartup` is `false` for adjacent reasons. `POST /api/admin/tenants/migrate` under `PlatformOwnerAccess` (`Program.cs:1538` — deliberately *not* `OwnerAccess`, since every signed-up user auto-owns their personal tenant, `:1908-1913`), with `dryRun=true` reporting pending counts. Failure is **per tenant**: one tenant's `42501` or unreachable pool member is a row in the result list, never an abort. Bounded concurrency (default 4) because each migration takes a non-pooled physical connection (`EfTenantDbMigrator.cs:56-62`).

- **D3 — `Rank` is `text COLLATE "C"`, and this is the single most silently-breakable decision in the epic.** 44-0's base-62 alphabet sorts `0-9 < A-Z < a-z` under ordinal comparison. Postgres agrees **only** under the `C` collation; under `en_US.UTF-8` it collates case-insensitively and `a` sorts before `B`. The failure is invisible in unit tests (which never touch Postgres) and produces an API order that disagrees with the board order. The column is declared `COLLATE "C"` in the migration, and AC4's Testcontainers test inserts real `Rank.Between` output and compares the SQL order to `StringComparer.Ordinal`.

- **D4 — Four tables in ONE migration, including `iterations` which nothing populates until 44-4.** Tenant migrations are the scarcest resource in this repo — before the sweep exists they were effectively one-way, and even after it they are an operator action per deploy. Splitting the tracker across two tenant migrations doubles that cost for no benefit. `iterations` ships empty.

- **D5 — No principal XOR on `work_items`, `projects` or `iterations`; the strong XOR on `tracker_preferences` only.** The XOR exists on `prompt_overrides` (`20260610013731_InitialTenant.cs:188`) and `acceptance_rules_overrides` because a *setting* has exactly one owning principal and the two planes must never join. A work item is content: it has a creator, an assignee and a project, all inside one tenant schema, and schema-per-tenant already supplies the isolation. A nullable `UserId` on `work_items` would encode a second ownership plane with no reader. `tracker_preferences` (default project, default kind, default board grouping) genuinely *is* per-principal configuration and takes the pattern exactly — including the **strong** form, not `ck_audit_records_principal_xor`'s weak `NOT (both NOT NULL)` (`20260619003624_AddAuditRecords.cs:42`) which permits both NULL, and including `.Annotation("Npgsql:NullsDistinct", false)` on the unique index without which the dedupe silently does nothing on the null half.

- **D6 — Key minting is a per-project counter under `FOR UPDATE`, not a Postgres sequence.** A sequence per project means DDL on every project create and orphaned sequences on delete. A `projects."NextNumber"` column selected `FOR UPDATE` inside the create transaction gives gap-free, monotone numbering with one row lock, and gap-free matters here: `TAM-1, TAM-2, TAM-4` looks like data loss to a user. `(ProjectId, Number)` unique and `Key` unique are the belt-and-braces; the concurrency test drives 50 parallel creates and asserts 50 distinct contiguous keys.

- **D7 — `ExternalRefJson` is one `jsonb` column, not four typed columns.** 44-8 owns the shape `(PlatformKind, RepoFullName, Number, Url)` and may need more once import is real. Freezing four columns two stories early is a second tenant migration waiting to happen. Null for native items; a partial index on `(ExternalRefJson->>'repoFullName', ExternalRefJson->>'number')` supports 44-8's already-linked skip.

- **D8 — `projects.RepositoryId` is a bare `Guid?` with no FK and no local `repositories` table.** 39-20 owns `repositories`, control-plane resident (its plan D1 `:32`). A cross-plane FK is not expressible, and creating a tenant-local repo registry to satisfy referential integrity would be the second repo registry the epic boundary table forbids. It is nullable, unenforced, and documented as pointing at 39-20's row. Until 39-20 lands, 44-8 resolves the repo through `tenant_platform_installations` (`Tamma.Data/Entities/TenantPlatformInstallation.cs:35`) instead.

- **D9 — Repositories go in `Tamma.Data/Repositories/`, DI in `Tamma.Data/DependencyInjection.cs`** — beside `AcceptanceRulesRepository` (`DependencyInjection.cs:159`) and `IConventionRepository` (`:160`), not inline in `Program.cs`. Services register in `Program.cs`; repositories do not. Following the split exactly avoids a review argument.

- **D10 — `Version` is an `int` bumped on write, not an EF `[Timestamp]` rowversion.** `AcceptanceRulesOverride.Version` (`Tamma.Data/Entities/AcceptanceRulesOverride.cs:26`) is the shipped precedent and 44-2 needs a value it can put in an ETag header and accept in an `If-Match`. A `byte[]` rowversion would need base64 plumbing for no gain.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Data/Entities/ProjectEntity.cs`** — `Id`, `Key` (the `PROJ` prefix, `WorkItemRef.IsValidProjectKey`), `Name`, `Description`, `RepositoryId` (`Guid?`, D8), `NextNumber` (int, D6), `ArchivedAt`, `CreatedByUserId`, `CreatedAt`, `UpdatedAt`, `Version`.

2. **CREATE `.../Entities/WorkItemEntity.cs`** — per story AC1. `Kind`/`Status`/`Priority`/`IssueType` stored as **wire strings**, matching how `TriageDecision` (`Tamma.Core/Documents/Types/TriageDecision.cs:122-131`) and `DocumentInstance.Status` already store theirs; the CHECK constraints are the enforcement, and the Core extensions are the parse boundary.

3. **CREATE `.../Entities/IterationEntity.cs`** — `Id`, `ProjectId`, `Name`, `StartsOn`, `EndsOn`, `Status` (`planned|active|closed`), `CapacityPoints` (`decimal?`), `CreatedAt`, `UpdatedAt`, `Version`. Populated by 44-4.

4. **CREATE `.../Entities/TrackerPreference.cs`** — `Id`, `UserId` (`Guid?`), `TenantId` (`Guid?`), `DefaultProjectId`, `DefaultKind`, `BoardGroupBy`, `CreatedBy`, `UpdatedBy`, `CreatedAt`, `UpdatedAt`, `Version`. Doc comment mirrors `AcceptanceRulesOverride.cs:3-11`'s dual-scoping explanation verbatim in shape.

5. **MODIFY `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs`** — four `DbSet`s in the `:52-102` block.

6. **MODIFY `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs`** — four `ToTable` blocks with indexes, CHECK constraints (`ck_work_items_status`, `ck_work_items_kind`, `ck_work_items_priority`, `ck_work_items_issue_type`, `ck_iterations_status`, `ck_tracker_preferences_principal_xor`), and the `NullsDistinct=false` unique index on `(UserId, TenantId)`.

7. **CREATE the migration** `Migrations/Tenant/<ts>_AddTrackerCore.cs` (+ Designer + snapshot). Hand-edit the generated `Up()` to add `COLLATE "C"` on `work_items."Rank"` (EF does not emit column collations for Npgsql by default) and to add the partial `ExternalRefJson` index.

8. **CREATE `Tamma.Data/Repositories/IWorkItemRepository.cs` + `WorkItemRepository.cs`** — `GetAsync(Guid id)`, `GetByKeyAsync(string key)`, `ListAsync(WorkItemQuery)` (project / status set / kind set / assignee / iteration / parent / text, ordered by `Rank`, keyset-paged on `(Rank, Id)`), `CreateAsync` (mints the key per D6), `UpdateAsync`, `SetRankAsync`, `SetStatusAsync`, `SetParentAsync`, `BulkSetRankAsync` (44-3's apply seam), `BulkSetIterationAsync` (44-4's).

9. **CREATE `IProjectRepository` + `IIterationRepository`** — CRUD + list, no mode split.

10. **CREATE `ITrackerPreferenceRepository` + impl** — the six paired methods of story AC6, predicates in `AcceptanceRulesRepository.cs:34,108` style, with the interface doc copying `IAcceptanceRulesRepository.cs:5-12`'s "PARALLEL — no method silently joins both planes" statement.

11. **MODIFY `Tamma.Data/DependencyInjection.cs`** — four `AddScoped` registrations after `:160`.

12. **CREATE `Tamma.Api/Endpoints/Admin/AdminTenantMigrationEndpoints.cs`** — `MigrateAll(bool dryRun, int? maxConcurrency)`. Enumerates `ControlPlaneDbContext.Tenants`, for each resolves the data source via `ITenantConnectionResolver`, reads pending migrations for `dryRun`, else calls `ITenantDbMigrator.MigrateTenantAppAsync`. Per-tenant try/catch → `{ tenantId, outcome, pendingBefore, error? }`. `SemaphoreSlim` bound.

13. **MODIFY `Tamma.Api/Program.cs`** — map `POST /api/admin/tenants/migrate` in the admin group with `.RequireAuthorization("PlatformOwnerAccess")` and `.RequireRateLimiting("ConfigWrite")`, beside the existing `/api/admin/tenant-databases` routes.

## Data & Migrations

One tenant migration, `AddTrackerCore`:

| Table | Notable |
|---|---|
| `projects` | `Key` unique per tenant; `NextNumber` for D6 minting; `RepositoryId Guid? ` unenforced (D8) |
| `work_items` | `Rank text COLLATE "C"` (D3); `(ProjectId, Number)` unique; `Key` unique; `ParentId` self-FK `ON DELETE RESTRICT`; `IterationId` FK `ON DELETE SET NULL`; indexes on `(ProjectId, Status, Rank)`, `(AssigneeUserId, Status)`, `(ParentId)`, `(IterationId)`, partial on `ExternalRefJson` |
| `iterations` | `(ProjectId, Name)` unique; `ck_iterations_status` |
| `tracker_preferences` | strong `ck_tracker_preferences_principal_xor`; unique `(UserId, TenantId)` with `NullsDistinct=false` |

`ParentId` is `RESTRICT`, not `CASCADE`: silently deleting a whole epic's subtree because someone deleted the epic is not recoverable, and 44-2 returns a 409 telling the caller to reparent or delete children first.

**No control-plane migration.** **No change to `domain_events`.**

## Events

None — 44-5. The sweep endpoint emits no DCB event either: it is a platform DDL operation with no tenant context to append into, and its result list is the record. (If Epic 37's audit catalog wants it, that is a `SensitiveActionCatalog` entry, not a DCB row.)

## Test Plan

| # | Test | Kind |
|---|---|---|
| 1 | `TrackerMigrationTests.All_four_tables_land_in_the_tenant_schema` | Testcontainers, `AcceptanceRulesOverridesMigrationTests.cs:49` shape |
| 2 | `TrackerMigrationTests.Status_check_constraint_matches_the_enum` | reflect `WorkItemStatus` wires, read `pg_constraint`, assert set equality |
| 3 | `TrackerMigrationTests.Kind_check_constraint_matches_the_enum` | as above |
| 4 | `TrackerMigrationTests.Rank_column_is_C_collated_and_sorts_ordinally` | insert 500 `Rank.Between` values shuffled; `ORDER BY "Rank"` == `OrderBy(Ordinal)` — **the AC4 test** |
| 5 | `TrackerMigrationTests.Preferences_xor_rejects_both_null_and_both_set` | both rejected (strong form) |
| 6 | `TrackerMigrationTests.Preferences_unique_index_dedupes_the_null_half` | proves `NullsDistinct=false` |
| 7 | `WorkItemRepositoryTests.Fifty_concurrent_creates_mint_contiguous_keys` | 50 parallel `CreateAsync`, assert distinct + contiguous 1..50 |
| 8 | `WorkItemRepositoryTests.Keyset_paging_is_stable_under_insertion` | page, insert mid-range, page again — no duplicate, no skip |
| 9 | `WorkItemRepositoryTests.Parent_delete_is_restricted` | FK violation surfaces, subtree intact |
| 10 | `TrackerPreferenceRepositoryTests.Planes_never_join` | a user row is invisible to `GetByTenantAsync` and vice versa |
| 11 | `TrackerOwnershipTests.No_work_item_query_filters_on_a_user_plane` | reflection/expression check over `IWorkItemRepository` — pins D5 |
| 12 | `AdminTenantMigrationTests.Sweep_reaches_a_pre_existing_tenant` | two tenants, one migrated by the creation path; sweep; assert the other gains the tables — **the AC9 test** |
| 13 | `AdminTenantMigrationTests.Sweep_is_idempotent` | second run reports `already-current`, no schema diff |
| 14 | `AdminTenantMigrationTests.One_failing_tenant_does_not_abort_the_sweep` | inject an unreachable connection for tenant B; A still migrates; B reported failed |
| 15 | `AdminTenantMigrationTests.DryRun_applies_nothing` | pending counts reported, `__TenantMigrationsHistory` unchanged |
| 16 | `AdminTenantMigrationTests.Requires_platform_owner` | member/tenant-admin → 403 |

## Definition of Done

- 16 tests green, Testcontainers Postgres 17.
- `TenantDbContextModelSnapshot.cs` regenerated and committed with the migration.
- `POST /api/admin/tenants/migrate` documented in the admin runbook with the `dryRun` workflow and the "run this after every deploy carrying a tenant migration" instruction.
- A `.dev/findings/` note recording that tenant migrations previously reached only new tenants, with the two call sites, so the next author does not rediscover it.
- Grep confirms no `repositories` table is created by this story (D8).

## Dependencies & Sequencing

- **Blocked by:** 44-0 (entities bind `WorkItemKind`/`WorkItemStatus`/`Rank`/`WorkItemRef`).
- **Blocks:** 44-2, 44-3, 44-4, 44-5, 44-7, 44-8, 44-9 — everything.
- **Shared-edit register:** `TenantDbContext.cs`, `TammaModelConfiguration.cs`, `TenantDbContextModelSnapshot.cs`, `Tamma.Data/DependencyInjection.cs`, `Program.cs`. The snapshot is a **single-author token** shared with any other in-flight tenant migration (Epic 39's and Epic 40's `agent_run_waits` are both noted as sharing it). Coordinate before starting.
- **Adjacent, not blocking:** 39-20's `repositories` (D8 is forward-compatible either way).

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The collation trap ships undetected.** EF's Npgsql provider does not emit column collations by default, so a regenerated migration silently drops `COLLATE "C"`. | Test 4 is a Testcontainers test that fails loudly; the hand-edit is called out in step 7 and in the DoD; a comment sits on the column in `TammaModelConfiguration`. |
| **The sweep is a new way to break production.** It runs DDL against every tenant. | `dryRun` default-off but documented first; per-tenant isolation (test 14); bounded concurrency; platform-owner only; `EfTenantDbMigrator` already idempotent and already used on both creation paths, so the sweep runs no code path that is not exercised on every tenant create today. |
| **Migration-snapshot contention.** Three epics have in-flight tenant migrations. | Named in the shared-edit register; rebase-and-regenerate rather than hand-merging the snapshot. |
| **Key minting under `FOR UPDATE` serializes project-level creates.** A bulk import (44-8, 44-9) of 900 items takes 900 sequential locks. | Acceptable at v1 volumes; `CreateManyAsync` takes the lock once and allocates a block, and 44-8/44-9 use it. Noted so the bulk paths are written against it rather than looping `CreateAsync`. |
| **`iterations` ships empty and could rot.** | 44-4 lands two stories later in the same epic; if it slips, the table is inert and costs nothing. Recorded rather than deferred, because a second tenant migration is the more expensive outcome (D4). |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–6 (entities, DbSets, model config, constraints) | 1.5 |
| Step 7 (migration + hand-edits + snapshot) | 0.75 |
| Steps 8–11 (four repositories + DI) | 1.5 |
| Steps 12–13 (the sweep endpoint) | 0.5 |
| Tests 1–11 (schema + repository, Testcontainers) | 1.0 |
| Tests 12–16 (the sweep, two-tenant fixtures) | 0.5 |
| Findings note, runbook, review | 0.25 |
| **Total** | **6.0** |
