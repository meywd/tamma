# Story 28-1 Implementation Plan — EF Migration Scripts (CP + Tenant + Elsa)

**Status**: Planned (2026-04-20)
**Story brief**: [`28-1-ef-migration-scripts.md`](./28-1-ef-migration-scripts.md)
**Epic 28 phase**: A (Foundation — serial)
**Branch**: `feat/story-28-1-ef-migrations`

---

## 1. Objective

Produce four independent EF Core migration sets — **ControlPlane**,
**Tenant**, **Global-Elsa**, **Per-Tenant-Elsa** — that build the
database-per-tenant schema from zero. Every other Epic 28 story is
blocked until these four sets compile, apply cleanly on fresh
Postgres 17, and produce the 14 CP tables + 15 tenant tables + the
Elsa schemas documented in Doc 01 §1.2. Ships idempotent bootstrap +
reset scripts so CI can wipe and replay, and seeds the three
`plans` rows that `tenants.PlanId` FK will reference.

## 2. Dependencies

Hard blockers:

- None — this is the Phase A starting point.
- Postgres 17 on the Hetzner VPS + in CI (Testcontainers).

Soft:

- **Existing `TammaDbContext`** stays intact for one release —
  migrations run against a *new* CP DbContext to avoid conflict.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` | 1 migration `20260421000000_InitialControlPlane.cs` — 14 CP tables + indexes + `api_keys.KeyPrefix` b-tree + `api_keys.RevokedAt` + `api_keys.RateLimitRpm`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/` | 1 migration `20260421000001_InitialTenant.cs` — 15 tenant tables. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/TenantElsa/` | Elsa-provider migrations for tenant Elsa schema. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Seeders/PlansSeeder.cs` | Inserts `free`, `team`, `enterprise` into `control_plane.plans`. |
| `/home/meywd/tamma/scripts/db/bootstrap-shared-dbs.sh` | Creates `tamma_control` + `tamma_global_elsa`; runs CP + global-Elsa migrations. |
| `/home/meywd/tamma/scripts/db/bootstrap-shared-dbs.ps1` | Windows equivalent. |
| `/home/meywd/tamma/scripts/db/reset-all.sh` | Drops shared DBs, re-bootstraps. Safe for dev/CI only. |
| `/home/meywd/tamma/scripts/db/reset-all.ps1` | Windows equivalent. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Data.Tests/MigrationValidationTests.cs` | Testcontainers: apply each set end-to-end on fresh Postgres 17 container. |
| `/home/meywd/tamma/docs/deployment/migration-ordering-28-1.md` | Runbook: which migration runs at which phase of the deploy. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Tamma.Data.csproj` | Add `<EFMigrationsFolder>Migrations/ControlPlane</EFMigrationsFolder>` per context. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | New keys: `ConnectionStrings:ControlPlane`, `ConnectionStrings:GlobalElsa`, `ConnectionStrings:DefaultTenantForDev`. |
| `/home/meywd/tamma/docker/docker-compose.yml` | Add `db-bootstrap` one-shot service that runs `bootstrap-shared-dbs.sh` before `api` + `elsa-global` start. |
| `/home/meywd/tamma/docker/docker-compose.prod.yml` | Same, gated on `BOOTSTRAP_ON_START=true`. |

## 5. Sequence of changes

### Step 1 — Schema skeletons via EF scaffolding (6h)

- Design all 14 CP entities + 15 tenant entities in separate `.cs`
  files (not yet mapped — just POCOs) so the migration author has
  a clear target.
- Write migration SQL as raw `migrationBuilder.Sql(...)` where
  CHECK constraints or non-standard indexes are needed (status
  enums, `api_keys.KeyPrefix` b-tree, `platform_events` partitioning
  hint for Story 28-10).
- `dotnet ef migrations add InitialControlPlane -c ControlPlaneDbContext -o Migrations/ControlPlane`.
- Same for tenant.
- **Commit**: `feat(db): initial CP + tenant EF migrations`.

### Step 2 — Elsa migrations (4h)

- Global-Elsa: Elsa's own EF provider auto-generates. Wire via
  `services.AddElsa().UseEntityFrameworkProvider(o => o.UseNpgsql(cs))`
  and run once locally to capture migration files.
- Per-tenant-Elsa: same migration set applied via provisioning
  workflow (28-5) using a template.
- **Commit**: `feat(db): Elsa migrations (global + per-tenant)`.

### Step 3 — Bootstrap + reset scripts (3h)

- `bootstrap-shared-dbs.sh`:
  - `psql -c "CREATE DATABASE tamma_control"` (IF NOT EXISTS via DO block).
  - Same for `tamma_global_elsa`.
  - `dotnet ef database update -c ControlPlaneDbContext --connection $CP_URL`.
  - `dotnet ef database update -c GlobalElsaDbContext --connection $GE_URL`.
  - JSON summary stdout: `{ db, migrationsApplied, durationMs }`.
- `reset-all.sh`: `DROP DATABASE` both shared DBs, re-run bootstrap.
- Both scripts idempotent (re-run is a no-op after first success).
- **Commit**: `scripts(db): bootstrap + reset shared DBs`.

### Step 4 — Plans seed (1h)

- `PlansSeeder.SeedAsync(ControlPlaneDbContext ctx)` inserts three
  rows with fixed IDs: `plan_free`, `plan_team`, `plan_enterprise`.
- Registered as a `IHostedService` that runs once on boot, guarded
  by an `EXISTS` check so re-run is no-op.
- **Commit**: `feat(db): plans seed data`.

### Step 5 — Migration validation tests (3h)

- `MigrationValidationTests`:
  - Spin up `Testcontainers.PostgreSQL` (fresh Postgres 17).
  - Run `bootstrap-shared-dbs.sh` equivalent (C# wrapper).
  - Assert all 14 CP + 15 tenant + Elsa tables exist via
    `information_schema.tables`.
  - Assert seeded plan rows.
  - Re-run the script; assert no duplicate applications.
- Runs in CI on every PR touching `Migrations/`.
- **Commit**: `test(db): migration validation (Testcontainers)`.

### Step 6 — Deploy wiring (2h)

- `docker-compose.yml`: `db-bootstrap` service with
  `depends_on: [postgres]` and `restart: 'no'`.
- Same for prod compose with env-gated flag.
- Deploy-ordering runbook: Postgres up → bootstrap → API + Elsa.
- **Commit**: `infra(compose): db-bootstrap one-shot service`.

### Step 7 — Migration-ordering doc (1h)

- `docs/deployment/migration-ordering-28-1.md`: sequence + rollback
  notes.
- **Commit**: `docs(deploy): migration ordering for 28-1`.

## 6. Test strategy

### Unit

- Entity mapping tests (one per entity): assert column types,
  nullability, defaults match Doc 01 §1.2.

### Integration

- `MigrationValidationTests` — full end-to-end against Testcontainers.
- Re-run the bootstrap script on an already-applied DB — assert no-op.
- Drop one table mid-run → assert `reset-all.sh` recovers.

### Manual

- Run on the Hetzner staging VPS against a fresh Postgres data volume.

## 7. Rollback plan

- **Fresh install**: no migration has been applied yet; `DROP DATABASE`
  reverts everything.
- **Post-applied**: these are *additive* migrations (new DBs, new
  tables). Rolling back means dropping the new databases. Safe on
  dogfood data since no tenants exist yet.
- **Non-reversible**: `PlansSeeder` inserts three FK targets. If
  you roll back the migration, the `plans` table is gone; any
  `tenants` row referencing it is already gone by cascade.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. CP + tenant schema | 6 |
| 2. Elsa migrations | 4 |
| 3. Bootstrap + reset scripts | 3 |
| 4. Plans seed | 1 |
| 5. Migration validation tests | 3 |
| 6. Deploy wiring | 2 |
| 7. Ordering doc | 1 |
| **Total** | **20** |

Brief estimate: 20-40h target 30h. Plan comes in at 20h because
Phase-2 RLS policies + `tamma_app` role creation are already in
`20260419021119_Phase2RlsAndTriggers.cs` and not duplicated here.

## 9. Open questions

- **Partitioning hint for `platform_events`**: Story 28-10 needs
  monthly partitions. Does 28-1 ship the parent table + first
  partition, or leave it to 28-10? Plan: parent table only; 28-10
  attaches its own first partition. Confirmed with 00-sequencing.md.
- **Seed plan IDs stability**: `plan_free` as string ID vs UUID?
  Doc 01 §4.1 says stable identifier — plan: UUIDv7 baked in seed so
  FK tests pass. Revisit if operators want human-readable IDs.
- **Migration file naming**: timestamps vs. semver-style numbers?
  Plan: EF default (timestamp prefix). Avoids clashes across
  contexts since they live in separate folders.
- **Per-tenant Elsa migration versioning**: how does 28-5 know which
  Elsa migration version to apply when provisioning a tenant?
  Proposed: hash the migration assembly, embed in provisioning
  workflow, fail if mismatch. Implementation deferred to 28-5.
