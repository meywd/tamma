# Story 28.1: EF Migration Scripts (CP + Tenant + Global-Elsa + Per-Tenant Elsa)

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Foundation
**Status**: MOSTLY DONE — see audit `docs/superpowers/plans/2026-05-29-epic-28-status-audit.md`. AC2 bootstrap + AC3 reset scripts now exist and are wired into both compose files (2026-05-31 follow-up below); the remaining residual is per-tenant Elsa runner verification. The 3-skipped-test gap from `bedf38a9` is resolved (2026-05-30 follow-up below): #1 re-enabled by PR D, #2/#3 kept as end-state contract tests blocked on Epic 30 / full db-per-tenant cutover.
**Priority**: High (every other Epic 28 story is blocked until four migration sets compile and apply cleanly)
**Estimated Effort**: L (20-40h) — target 30h

## User Story

As a **platform engineer**, I want **four independent EF Core migration
sets that produce the control-plane, tenant, global-Elsa, and per-tenant
Elsa schemas from zero**, so that **every downstream Epic 28 story can
compile against a real schema and the provisioning workflow has something
to apply when it spins up a new tenant**.

## Acceptance Criteria

### AC1: Four migration assemblies exist and are independently applicable

- [ ] `ControlPlaneDbContext` migrations under
      `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` produce
      the 14 CP-resident tables from Doc 01 §1.2: `users`,
      `refresh_tokens`, `password_reset_tokens`, `tenants`,
      `tenant_memberships`, `user_invites`, `api_keys` (CP scope),
      `github_installations`, `github_installation_repos`,
      `platform_events`, `platform_queued_tasks`,
      `platform_email_outbox`, `token_revocations`, `plans`.
- [ ] `TenantDbContext` migrations under `Migrations/Tenant/` produce
      the tenant-resident tables from Doc 01 §1.2: `agent_configs`,
      `prompt_overrides`, `provider_health`, `provider_diagnostics`,
      `sanitization_rules`, `workflow_definitions` (tenant-authored),
      `workflow_instances` (tenant runs), `domain_events`, `queued_tasks`,
      `email_outbox`, `mentorship_sessions`, `mentorship_events`,
      `junior_developers`, `stories`, `api_keys` (tenant scope).
- [ ] Global-Elsa DB migrations run via Elsa's EF provider against the
      connection string `ConnectionStrings:GlobalElsa` and land Elsa's
      standard `WorkflowDefinitions`, `WorkflowInstances`, `Bookmarks`,
      `Triggers`, `WorkflowExecutionLogRecords`, `ActivityExecutions`,
      `AgentDefinitions`, `ApiKeysDefinitions`, `ServicesDefinitions`
      tables in `tamma_global_elsa`.
- [ ] Per-tenant Elsa DB migrations run via Elsa's EF provider against
      a tenant-resolved connection string and land the same Elsa schema
      in `tamma_tenant_<guid32>_elsa`.

### AC2: Idempotent bootstrap script for shared DBs

- [ ] A script `scripts/db/bootstrap-shared-dbs.{sh,ps1}` creates (if
      missing) `tamma_control` and `tamma_global_elsa`, runs the four
      migration sets that apply at boot (CP + global-Elsa; tenant +
      tenant-Elsa are applied by the provisioning workflow per-tenant),
      and is safe to re-run.
- [ ] Script exits non-zero on any migration failure and emits a
      structured summary `{ db, migrationsApplied, durationMs }`.
- [ ] `docker-compose.yml` and `docker-compose.prod.yml` are updated so
      the Postgres service entrypoint (or a one-shot `db-bootstrap`
      service) runs the script before `api` and `elsa-global` start.

### AC3: Wipe-and-replay script for CI / local dev

- [ ] `scripts/db/reset-all.{sh,ps1}` drops and recreates
      `tamma_control` + `tamma_global_elsa`, then invokes the bootstrap
      script. Does **not** touch per-tenant DBs (those are
      workflow-provisioned).
- [ ] Used by `pnpm test:integration` setup via Testcontainers fixture
      or a dedicated CI job step.
- [ ] Running the script twice in succession produces an identical final
      schema (idempotency assertion — row counts in
      `__EFMigrationsHistory` match the expected set).

### AC4: Seed data for the `plans` table

- [ ] Seed inserts three default rows into `control_plane.plans`:
      `free`, `team`, `enterprise`, with matching identifiers for the
      `tenants.PlanId` FK referenced in Doc 01 §4.1.
- [ ] Seed is applied by the CP migration set (EF `HasData` or a
      dedicated `DataSeeder` hosted service run at migration-apply
      time) and is idempotent (no duplicate inserts on re-run).
- [ ] Each seed row has a stable `id` so `tenants.PlanId` FK values are
      deterministic across environments.

### AC5: Strict typing and constraints

- [ ] `tenants.Status` is backed by a `CHECK` constraint enumerating the
      valid states from Doc 01 §10.2:
      `pending_verification | provisioning | active | delete_requested | deleting | deleted | failed | suspended`.
- [ ] `tenants.KekVersion` is `smallint NOT NULL DEFAULT 1` per Doc 01
      §8.1 and Doc 04 §4.3.
      > **Accepted spec divergence (2026-06-05):** shipped as `integer NULL`
      > (`Property<int?>("KekVersion")` in `TammaModelConfiguration.cs`). A
      > `null` KekVersion is the legacy-row heuristic path in
      > `AesGcmConnectionStringDecryptor`; the wider, nullable column is
      > functionally compatible and no migration to `smallint NOT NULL` is
      > planned. Verification: `2026-05-30-epic-28-residual-verification.md`.
- [ ] `tenants.EncryptedConnectionString` is `bytea` (nullable only
      during `pending_verification`; enforced by a partial `CHECK`
      constraint — `Status = 'pending_verification' OR
      EncryptedConnectionString IS NOT NULL`).
- [ ] `tenants.EncryptedElsaConnectionString` mirrors the same shape
      per Doc 04 §4.3.
- [ ] `api_keys` CP table has a `Scope` column constrained to
      `('platform','user')`; tenant DB `api_keys` has `Scope = 'tenant'`
      enforced by a `CHECK` constraint per Doc 01 §1.4.

### AC6: Required indexes

- [ ] `api_keys.KeyPrefix` in both CP and tenant DB — non-unique b-tree,
      used by `ApiKeyAuthHandler` prefix-scan per Doc 01 §3.1.
- [ ] `platform_queued_tasks (Status, NextAttemptAt)` — composite
      b-tree, used by the `FOR UPDATE SKIP LOCKED` leasing query (Doc 01
      §1.2 row 24, Story 28-6).
- [ ] `platform_email_outbox (Status, CreatedAt)` — composite b-tree for
      the outbox scan loop (Doc 01 §1.2 row 26, Story 28-6).
- [ ] `platform_events ((tenant_id), (type), ((tags->>'step')),
      ((tags->>'attempt')))` — partial unique index on
      `TENANT.PROVISION.STEP_*` events per Doc 03 §2.4.
- [ ] `platform_events (tenant_id, created_at DESC)` — general tenant
      timeline scan.
- [ ] `tenant_memberships (user_id, tenant_id)` — unique composite for
      membership lookup during `/auth/me` and `/auth/switch-org` per
      Doc 01 §2.2.

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/01-control-plane-split.md` §1 (entity
    placement), §6 (naming), §8 (KEK envelope), §10.2 (status state
    machine).
  - `plans/db-per-tenant/02-elsa-two-tier.md` §7 (Elsa DB per tenant).
  - `plans/db-per-tenant/03-async-tenant-provisioning.md` §2.4 (partial
    unique index on provisioning events).
  - `plans/db-per-tenant/04-connection-pool-and-delete.md` §4.3 (KEK
    schema additions).
- **Migration assembly layout**: `apps/tamma-elsa/src/Tamma.Data/` with
  two new subdirectories `Migrations/ControlPlane/` and
  `Migrations/Tenant/`. Each keeps its own
  `__EFMigrationsHistory` (different DB) — configure via
  `MigrationsHistoryTable("__ef_migrations_history", "control_plane")`
  and `MigrationsHistoryTable("__ef_migrations_history", "public")`
  respectively (per Doc 04 §3.2).
- **Migration numbering**: reserve `050_cp_initial`, `051_cp_seed_plans`,
  `052_tenant_initial`. Global-Elsa and per-tenant-Elsa run Elsa's own
  EF migrations unchanged — no Tamma-authored migrations on those DBs.
- **No cross-DB FKs**: per Doc 01 §3.1–3.3, all cross-DB references are
  raw `Guid` columns without FK constraints. Migration generation must
  not emit cross-DB `REFERENCES` clauses.
- **Down scripts**: every migration has an EF `Down()` method that
  reverses schema changes, per Phase 1 rollback strategy in
  `00-sequencing.md`.

## Dependencies

- **Blocks**: 28-2, 28-3, 28-4, 28-5, 28-6, 28-7, 28-8, 28-9, 28-10,
  28-11, 28-12 (the entire rest of Epic 28).
- **Blocked by**: none — this is the first story in the epic.
- **External**: EF Core 8+, Npgsql.EntityFrameworkCore.PostgreSQL,
  Elsa.EntityFrameworkCore.PostgreSql (Elsa migration provider already
  in stack).

## Test Plan

### Unit tests

- Migration `Up()` / `Down()` round-trip for each of the three
  Tamma-authored migrations (CP initial, CP seed, tenant initial) —
  verify `Down()` leaves a clean schema using an in-memory model diff.
- `CHECK` constraint assertions: insert a `tenants` row with
  `Status = 'bogus'`, assert the DB rejects it with `23514`.
- `api_keys` tenant-side `Scope` constraint rejects non-`tenant` values.

### Integration tests (Testcontainers.PostgreSQL)

- Fresh Postgres 17 container → run bootstrap → assert CP schema + seed
  rows + global-Elsa schema present. Assert `__EFMigrationsHistory`
  counts match expected.
- Run bootstrap twice in succession → no duplicate seed rows, no
  migration re-application (twice-run-clean test, corresponds to epic
  success metric #1).
- Apply CP + tenant migrations on separate DBs in the same container,
  verify both schemas coexist independently (no cross-DB table leaks).
- Run `reset-all` script, then bootstrap, assert schema matches golden
  file snapshot.
- Apply all six required indexes, verify via `pg_indexes` query.

### Manual verification

- `docker compose down -v && docker compose up` on a clean environment
  produces a running stack with all four schemas in place and no
  migration warnings in logs.
- `psql -d tamma_control -c "\d tenants"` shows the `CHECK` constraint
  on `Status`.
- `psql -d tamma_control -c "SELECT * FROM plans"` returns three rows.

## Definition of Done

- [ ] Acceptance criteria all green
- [ ] Unit + integration tests added, suite passes
- [ ] No new CodeQL alerts
- [ ] Design-doc references updated if the impl deviated
- [ ] Reviewed by a second engineer (cross-stream)

## Risks / Open Questions

- **Migration number collisions with in-flight PRs.** Coordinate the
  `050–052` block with any open auth-foundation PR before merging —
  Phase 1 rollback strategy in `00-sequencing.md` flags this as the #1
  risk for Story 28-1.
- **Elsa migration version pinning.** Global-Elsa and per-tenant Elsa
  DBs share an Elsa NuGet version — a future Elsa upgrade must run
  migrations against every per-tenant Elsa DB. The bootstrap script
  covers the shared DBs; per-tenant Elsa upgrades are a follow-up
  concern (out of scope for this story, flagged here so it isn't lost).
- **Seed plan IDs.** Design docs don't pin specific UUIDs for the three
  seed plan rows. Choose deterministic UUIDs (e.g. `00000000-0000-0000-0000-00000000000{1,2,3}`)
  and document them in a header comment on the seed migration so
  integration tests can rely on them.

## Closed by 2026-05-30 follow-up — disposition of 3 skipped end-state tests

Audit `2026-05-29-epic-28-status-audit.md` flagged commit `bedf38a9`
("skip 3 aspirational 28-1 tests"). All three tests were re-examined
post PR D (`c90e03a6`, the 15-entity move from CP → Tenant). Final
verdict:

| # | Test                                                                                                          | Verdict                                          | Rationale                                                                                                                                                                                                                                                                                                          |
|---|---------------------------------------------------------------------------------------------------------------|--------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1 | `Epic28.ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities`                               | **Re-enabled (passing)** — closed by PR D       | PR D shipped the 15-entity move (11 business POCOs + 4 mentorship). The `[Ignore]` was removed and the assertion list expanded to the final 25 CP-resident tables (Doc 01 §1.2 base 14 + analytics + alerts + KEK rotations + admin impersonations + bootstrap + Story 31-2/31-7 platform-installations & webhooks). |
| 2 | `Epic28.ControlPlaneDbContextModelTests.Tenants_Cranl_Columns_Are_Ignored_On_NewContext`                      | **Kept `[Ignore]`** — blocked on Epic 30        | Cranl columns (`CranlDatabaseUrlEncrypted` et al.) are *load-bearing in production*: `LruPooledTenantConnectionResolver` reads them to route per-request DB connections (Story 29-10 stopgap). Removing them today would break tenant routing. Re-enable when Epic 30 ships pluggable infra backends + an alternative routing column. |
| 3 | `Epic28.TenantDbContextModelTests.Tenant_Resident_Entities_Have_No_TenantId_Column`                           | **Kept `[Ignore]`** — blocked on full cutover   | Production today routes most tenants via `StubTenantConnectionResolver` onto a shared central Postgres (see `CLAUDE.md` "Routing (current state)"). The `TenantId` predicate in tenant repositories is the only isolation plane while the shared-DB topology is in play. Re-enable when every tenant has a dedicated physical DB (Epic 28 full cutover or Epic 30 removes the shared-DB seam). |

Tests #2 and #3 encode the **end-state contract** for the db-per-tenant
architecture and intentionally remain in the suite as living specs.
Each `[Ignore]` attribute names the owning epic so the test re-enables
deterministically when the corresponding blocker lands. They are not
"aspirational with no plan" — they are aspirational with a plan that
lives in another epic.

**Status correction:** the audit flagged "3 skipped tests" but PR D
(`c90e03a6`, dated 2026-04-28, six days after `bedf38a9`) had already
re-enabled test #1 when the 15-entity move shipped. Today's count is
2 skipped, both with cross-epic ownership.

**Verification:**
```
sg docker -c "dotnet test apps/tamma-elsa/Tamma.sln \
  --filter 'FullyQualifiedName~Epic28.ControlPlaneDbContextModelTests|FullyQualifiedName~Epic28.TenantDbContextModelTests'"
# Expect: 13 passed, 2 skipped
```

## Closed by 2026-05-31 follow-up — AC2 bootstrap + AC3 reset scripts (shared-infra reconciliation)

AC2 and AC3 required `scripts/db/bootstrap-shared-dbs.{sh,ps1}` and
`scripts/db/reset-all.{sh,ps1}`, neither of which existed. They are now
shipped — but **reconciled against the real current topology**, not the
aspirational two-database design the AC text was written against.

### Topology reality (why the AC text needed reconciling)

The AC2/AC3 text assumes two separate databases — `tamma_control` and
`tamma_global_elsa`. The actual deployment is **shared-infrastructure
mode**:

- `docker-compose.yml` + `docker-compose.prod.yml` define a **single
  `postgres` service** with **one database, `tamma`**.
- Both `tamma-api` and `elsa-server` use the same `Database=tamma`
  connection string — Elsa's tables live in `tamma`, there is no separate
  `tamma_global_elsa` DB.
- `ConnectionStrings:ControlPlane` is deliberately unset (the prod
  `tamma-api` block sets `Tamma__RequireTenantIsolation=false`); all
  tenants + control-plane data share `tamma` via Phase-3 RLS. Reconfirmed
  by the 2026-05-31 hotfix (`146c354e`).
- **Migrations are applied by the apps themselves at boot**, not by a
  shell script: `Tamma.Api/Program.cs` calls
  `dbContext.Database.Migrate()`; `Tamma.ElsaServer/Program.cs` sets
  `ef.RunMigrations = true` on Elsa's EF modules. The EF migration
  assemblies are the single source of truth for the schema.

### How the scripts reconcile aspirational → real

- The scripts **ensure the databases that actually exist today** (`tamma`)
  rather than blindly creating `tamma_control` + `tamma_global_elsa`. DB
  names are **parameterised** (`TAMMA_CONTROL_DB`, `TAMMA_GLOBAL_ELSA_DB`),
  both defaulting to the shared `tamma`. They de-dupe so shared mode emits
  one summary line, not two for the same DB.
- **Forward-compatible with the per-tenant-DB cutover**: when
  `tamma_control` / `tamma_global_elsa` become real databases, point those
  two env vars at them and the identical create-if-missing + summary logic
  applies — no script change needed.
- "Apply migrations" is honoured by the app's own boot-time
  `Database.Migrate()` / `RunMigrations`. The bootstrap script's job in the
  normal Docker flow is to **guarantee the target databases exist** before
  the api / elsa-server containers start. For fresh-cluster / CI flows that
  want the schema applied *without* booting the app, set
  `TAMMA_RUN_EF_MIGRATIONS=1` and the script drives `dotnet ef database
  update` (best-effort; needs the .NET SDK on the host) and reports the
  real applied-migration delta. Otherwise the summary reports
  `migrationsApplied: 0` (DB-existence-only) and the app self-migrates.

### AC2 — `bootstrap-shared-dbs.{sh,ps1}`

- Idempotent: each DB guarded by `SELECT 1 FROM pg_database WHERE datname
  = …` before `CREATE DATABASE`. Safe to re-run (second run is a no-op).
- Exits non-zero on any failure (`set -euo pipefail` + explicit checks).
- Emits one JSON-lines summary per DB on stdout:
  `{ "db": "…", "migrationsApplied": N, "durationMs": N }`.
- Reads `PGHOST`/`PGPORT`/`PGUSER`/`PGPASSWORD` (with `DB_PASSWORD`
  fallback) and the two DB-name env vars, all with sensible defaults.
- Bounded `pg_isready` wait so the one-shot service tolerates a slow
  Postgres start.

### AC3 — `reset-all.{sh,ps1}`

- Drops (`DROP DATABASE IF EXISTS`, after terminating other backends) +
  recreates the shared DBs by delegating to the bootstrap script (single
  source of truth for create + summary).
- **Never touches per-tenant DBs**: a hard guard refuses any name matching
  `tamma_tenant_*`. (There are none in shared mode; the guard protects the
  forward-compatible path.)
- Safety gate: refuses to run unless `TAMMA_RESET_CONFIRM=yes` or
  `--force`/`-Force`, and always refuses when
  `ASPNETCORE_ENVIRONMENT=Production`.
- Idempotent final schema: `DROP IF EXISTS` + bootstrap create-if-missing
  + the same EF migration set ⇒ running twice yields the same result.

### Compose wiring — one-shot `db-bootstrap` service

Chose the **one-shot service** approach (per AC2's stated preference) over
mangling the Postgres entrypoint:

- Added a `db-bootstrap` service (`postgres:17-alpine`, `restart: 'no'`) to
  **both** `docker-compose.yml` and `docker-compose.prod.yml`. It bind-
  mounts the repo-root `scripts/db` dir (`../../scripts/db:/scripts/db:ro`)
  and runs `bootstrap-shared-dbs.sh`, `depends_on` postgres
  `service_healthy`.
- `elsa-server` and `tamma-api` now `depends_on` `db-bootstrap` with
  `condition: service_completed_successfully` (Compose ≥ 2.20; the local
  toolchain is v2.40). This guarantees the DB exists before either app
  boots and self-migrates — no deadlock, because the bootstrap container
  always runs to completion and exits 0.
- Because the apps self-migrate, the practical effect in shared mode is an
  ordering + existence guarantee; the script becomes load-bearing on a
  fresh cluster, in CI, and the day the `tamma_control` /
  `tamma_global_elsa` split lands.

### Validation performed

- `bash -n` clean on both `.sh` scripts.
- `docker compose -f docker-compose.yml config -q` and
  `… -f docker-compose.prod.yml config -q` both pass (only the pre-existing
  obsolete-`version` warning + unset-var warnings — no errors; the
  `service_completed_successfully` condition is accepted).
- Verified the `../../scripts/db` bind mount resolves to
  `/home/meywd/tamma/scripts/db` where the executable script lives.

### Needs human verification

- **shellcheck** was not available in this environment — only `bash -n`
  syntax validation ran. Recommend a shellcheck pass in CI.
- **PowerShell scripts** were not statically validated (no `pwsh` on this
  host). The `.ps1` files mirror the `.sh` logic but should be parsed
  on a Windows/pwsh box before relying on them.
- The compose dependency assumes the apps self-migrate on boot (confirmed
  in `Program.cs`); confirm this remains true if migration handling is
  ever refactored.
