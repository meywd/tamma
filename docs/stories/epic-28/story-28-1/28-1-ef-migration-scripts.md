# Story 28.1: EF Migration Scripts (CP + Tenant + Global-Elsa + Per-Tenant Elsa)

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Foundation
**Status**: Draft
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
