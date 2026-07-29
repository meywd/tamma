# Story 44-1: Storage, Repositories, and the Migrate-All-Provisioned-Tenants Sweep

Status: drafted

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

## Architectural Context (READ FIRST)

- **The migration-reach gap is real and has no operational escape hatch today.** `ITenantDbMigrator.MigrateTenantAppAsync` (`apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantDbMigrator.cs:33`, impl `Tamma.Data/Pooling/EfTenantDbMigrator.cs:25`) has **exactly two production call sites**, both creation-only:
  - `Tamma.Api/Services/Provisioning/TenantProvisioningService.cs:172`, inside `ProvisionAsync` (`:147`), reached only from `Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs:176-177`. It runs **only when `password is not null`** (a freshly minted role); an idempotent re-run explicitly skips it (`:167-176`).
  - `Tamma.Activities/TenantLifecycle/MigrateTenantDatabaseActivity.cs:53`, step 4 of `CreateTenantWorkflow` (`Tamma.ElsaServer/Workflows/CreateTenantWorkflow.cs:158-160`).
  There is no admin endpoint (`Tamma.Api/Endpoints/Admin/` contains no `migrate` handler), no hosted service and no queued-task handler that fans DDL over tenants. `Program.cs:3278` migrates the **control plane** only.
- **`EfTenantDbMigrator` is already idempotent and safe to call repeatedly.** It derives the schema from the connection string's `Search Path` (`:47`), forces `Pooling=false` on the migration connection (`:56-62`), pins the per-tenant history table `__TenantMigrationsHistory` (`:64-67`), runs an idempotent `CREATE SCHEMA IF NOT EXISTS` safety net (`:83-92`) and then `MigrateAsync` (`:97`). **The sweep is a caller, not a redesign.**
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

Still open (owned by OTHER lanes — do not close this section until they land):

- **`WorkItemEntity.Version` is not an EF concurrency token** (`IsConcurrencyToken` missing in `TammaModelConfiguration`/snapshot — model-config lane). Concurrent rekeys can interleave read-modify-write on `PreviousKeys` and silently LOSE key history. **Must land before 44-2 exposes rekey over HTTP.**
- **Sweep endpoint hygiene** (`Program.cs` — coordinator's lane): `dryRun` defaults to `false` (a bare POST applies DDL fleet-wide); no single-flight guard (two concurrent sweeps double-migrate); the sweep runs synchronously in-request (a large fleet outlives HTTP timeouts); the pool's `CommandTimeout=30s` applies to migration DDL and will spuriously fail heavy migrations.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
| 2026-07-29 | 1.0.1   | Adversarial-review follow-ups section (fixes landed in the tracker-storage lane; deferred items listed) | Claude |
