# Story 28.2: Split `TammaDbContext` into `ControlPlaneDbContext`

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Foundation
**Status**: DONE — see audit `docs/superpowers/plans/2026-05-29-epic-28-status-audit.md` (TammaDbContext deleted outright; stronger than AC3 required)
**Priority**: High (the CP DbContext must exist before any story that
writes `platform_events`, reads `tenants`, or touches `users` can land;
Phase 1 serial critical path)
**Estimated Effort**: M (8-20h) — target 16h

## User Story

As a **platform engineer**, I want **a dedicated `ControlPlaneDbContext`
that owns exactly the 14 control-plane tables**, so that **every auth
handler, admin endpoint, and tenant-lifecycle workflow has a clear,
tenant-free data path that cannot accidentally pull in per-tenant
business data**.

## Acceptance Criteria

### AC1: `ControlPlaneDbContext` class owns 14 CP tables

- [ ] New class `ControlPlaneDbContext : DbContext` under
      `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` exposes
      `DbSet<T>` for each of the 14 CP-resident entities from Doc 01
      §1.2 Appendix A: `Users`, `RefreshTokens`, `PasswordResetTokens`,
      `Tenants`, `TenantMemberships`, `UserInvites`, `ApiKeys` (CP
      scope), `GitHubInstallations`, `GitHubInstallationRepos`,
      `PlatformEvents`, `PlatformQueuedTasks`, `PlatformEmailOutbox`,
      `TokenRevocations`, `Plans`.
- [ ] No tenant-scoped `DbSet` is present (no `AgentConfig`,
      `DomainEvent`, `QueuedTask`, `EmailOutboxMessage`, `MentorshipSession`,
      etc.). Those live on `TenantDbContext` (Story 28-3).
- [ ] `OnModelCreating` applies the schema decisions from Story 28-1:
      `CHECK` constraints on `tenants.Status`, seed `HasData` for the
      three `Plans` rows, unique indexes on `tenant_memberships`,
      etc.
- [ ] `ControlPlaneDbContext` has **no** `HasQueryFilter` calls — CP is
      tenant-agnostic, there is nothing to filter by.

### AC2: DI registration swap in API + global-Elsa hosts

- [ ] `Program.cs` in `apps/tamma-elsa/src/Tamma.Api/` registers
      `ControlPlaneDbContext` via `AddDbContext` using the pattern from
      Doc 04 §3.2: static connection string from
      `ConnectionStrings:ControlPlane`.
- [ ] `Program.cs` in `apps/tamma-elsa/src/Tamma.ElsaServer/` (the
      global-Elsa host that will run `CreateTenantWorkflow` in Story
      28-5) also registers `ControlPlaneDbContext` against the same
      connection string — the workflow reads and writes the `tenants`
      row and `platform_events`.
- [ ] All existing handlers that currently inject `TammaDbContext`
      (auth endpoints, `/tenants` listing, `/users` admin) are migrated
      to inject `ControlPlaneDbContext`. No handler accesses both
      contexts in the same scope yet (that comes with Story 28-3 for
      tenant-scoped endpoints).

### AC3: Obsolete marker on `TammaDbContext`

- [ ] The existing `TammaDbContext` class is annotated
      `[Obsolete("Use ControlPlaneDbContext for CP data, TenantDbContext
      for tenant data. Will be removed in Story 28-3.", error: false)]`.
- [ ] Build succeeds with warnings for any remaining callers — these
      callers are migrated in Story 28-3 and `TammaDbContext` is deleted
      there.
- [ ] An explicit unit test asserts that every non-test caller of
      `TammaDbContext` in `apps/tamma-elsa/src/Tamma.Api/` has been
      migrated to `ControlPlaneDbContext` (grep-based or Roslyn
      analyzer — pick the cheaper option; documented in the story
      retrospective).

### AC4: New `ConnectionStrings:ControlPlane` config key

- [ ] `appsettings.json` and `appsettings.Development.json` gain a
      `ConnectionStrings:ControlPlane` entry pointing at
      `tamma_control` on the shared Postgres host.
- [ ] The old `ConnectionStrings:DefaultConnection` is preserved (still
      used by obsolete `TammaDbContext`) and removed in Story 28-3.
- [ ] Documentation under `docs/deployment/` (or the appropriate
      ops runbook) records the new connection-string key and the
      corresponding environment-variable override format
      (`ConnectionStrings__ControlPlane`).

### AC5: Docker Compose wiring

- [ ] `docker-compose.yml` and `docker-compose.prod.yml` define
      `tamma_control` as a created DB at Postgres boot (via the
      bootstrap script from Story 28-1) and pass
      `ConnectionStrings__ControlPlane` to the `api` and `elsa-global`
      services.
- [ ] Existing `tamma` DB is kept for the obsolete `TammaDbContext`
      until Story 28-3 deletes it.
- [ ] A fresh `docker compose down -v && docker compose up` boot
      produces a running API that serves `/auth/login` against
      `ControlPlaneDbContext`.

### AC6: CP-only endpoints still pass their existing tests

- [ ] All auth endpoint tests under
      `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/Auth*` pass
      against the new context without behaviour change.
- [ ] Admin tenant-list endpoint (`GET /api/v1/tenants` for the
      current user's memberships) still returns the expected shape.
- [ ] `/auth/me` response shape is unchanged (it was already CP-only —
      see Doc 01 §2.2.3).

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/01-control-plane-split.md` §1.2 (entity
    placement table), §2 (auth runs on CP only), §5.2 (CP events),
    Appendix A (ADR summary).
  - `plans/db-per-tenant/04-connection-pool-and-delete.md` §3
    (DbContext registration, naming mapping "`ControlPlaneDbContext`
    replaces the current single `TammaDbContext` for non-tenant data").
- **File layout**:
  - `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` — new.
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` — marked
    `[Obsolete]`, remains present until 28-3.
  - `apps/tamma-elsa/src/Tamma.Data/Entities/` — entity classes
    reused; no duplication. The split is at the DbContext layer,
    not the entity layer.
- **Registration site**: `Program.cs` for API and
  `Tamma.ElsaServer/Program.cs` for global Elsa. Both call
  `services.AddDbContext<ControlPlaneDbContext>(options =>
  options.UseNpgsql(config.GetConnectionString("ControlPlane"),
  npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history",
  "control_plane")));` per Doc 04 §3.2.
- **Migration history table**: CP uses
  `control_plane.__ef_migrations_history` schema/table combination so
  CP and the obsolete `TammaDbContext` don't collide during the
  transition.
- **Phase 1 deploy gate**: Per `00-sequencing.md`, this story must land
  such that existing CP-only endpoints still pass their current test
  suite before Phase 2 can open.

## Dependencies

- **Blocks**: 28-3, 28-5, 28-9, 28-10 (Per-request CP access is the
  shared foundation for all of these).
- **Blocked by**: 28-1 (CP migrations must exist).
- **External**: EF Core 8+, existing `Tamma.Data` assembly.

## Test Plan

### Unit tests

- `ControlPlaneDbContext` model builder unit test: assert each of the
  14 `DbSet<T>` is present and no tenant-scoped `DbSet` sneaks in
  (Roslyn reflection test iterating `typeof(ControlPlaneDbContext)
  .GetProperties()`).
- `OnModelCreating` applies `CHECK` constraint on `tenants.Status`
  (assert via `ModelBuilder` inspection).
- Obsolete-marker test: `typeof(TammaDbContext).GetCustomAttributes<
  ObsoleteAttribute>()` returns the expected attribute with
  `IsError = false`.

### Integration tests (Testcontainers.PostgreSQL)

- Spin up Postgres, apply CP migrations, construct
  `ControlPlaneDbContext`, round-trip a `Users` insert + read.
- Round-trip `Tenants` insert with all valid `Status` values; assert
  `'bogus'` value is rejected by the DB.
- `/auth/login`, `/auth/register`, `/auth/me`, `/tenants` (list)
  end-to-end tests pass against the new context.
- Assert `ControlPlaneDbContext` with no `ITenantContext` in DI works
  (it should — CP is tenant-free).
- Assert the old `TammaDbContext` still builds and responds (obsolete
  but not deleted) — regression test.

### Manual verification

- `docker compose up` boots API successfully; `GET /auth/me` after
  login returns a valid response.
- Inspect `pg_stat_activity` on `tamma_control` and confirm the API
  opens connections to the new DB.

## Definition of Done

- [ ] Acceptance criteria all green
- [ ] Unit + integration tests added, suite passes
- [ ] No new CodeQL alerts
- [ ] Design-doc references updated if the impl deviated
- [ ] Reviewed by a second engineer (cross-stream)

## Risks / Open Questions

- **Reflection/DI callers of `TammaDbContext`.** Phase 1 rollback
  strategy in `00-sequencing.md` calls out implicit consumers via
  reflection or DI naming. A grep pass plus a CI-enforced assertion is
  required before marking obsolete — doing it as part of this story so
  Story 28-3's `TammaDbContext` delete isn't blocked by surprise
  callers.
- **Transitional dual-DbContext complexity.** During the Story 28-2 →
  28-3 window, the codebase has three DbContexts (`TammaDbContext`
  obsolete, `ControlPlaneDbContext` new, `TenantDbContext` coming).
  The obsolete marker communicates intent but doesn't prevent fresh
  `TammaDbContext` usage. Acceptable because the window is short (two
  stories apart) and the next story deletes the obsolete context.
- **Design docs are silent on whether `Plans` lives in CP or is
  hard-coded.** Decision in Story 28-1 puts `plans` in CP as a seeded
  table so billing-plan edits don't require a redeploy; this story
  inherits that decision. If the product team later wants `Plans`
  moved to config, a follow-up migration removes the table.
