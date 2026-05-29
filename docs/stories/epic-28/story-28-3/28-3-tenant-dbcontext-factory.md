# Story 28.3: `TenantDbContext` Factory with Runtime Connection Routing

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Foundation
**Status**: DONE — see audit `docs/superpowers/plans/2026-05-29-epic-28-status-audit.md` (AC3 release-build-throws semantics covered by `Replace`-on-real-resolver pattern in `AddTenantConnectionPool`)
**Priority**: High (the factory is the seam every tenant-scoped handler
uses; Story 28-4 replaces the stub with the real resolver)
**Estimated Effort**: M (8-20h) — target 14h

## User Story

As a **platform engineer**, I want **a `TenantDbContext` + factory
interface (`ITenantDbContextFactory`) that constructs a per-request,
per-tenant `DbContext` from an injected `NpgsqlDataSource`**, so that
**every tenant-scoped handler has a single, tested entry point for
tenant data access and Story 28-4 can swap in the real resolver without
touching handler code**.

## Acceptance Criteria

### AC1: `TenantDbContext` class owns the tenant-resident entities

- [ ] New class `TenantDbContext : DbContext` under
      `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` exposes
      `DbSet<T>` for each of the tenant-resident entities from
      Doc 01 §1.2: `AgentConfigs`, `PromptOverrides`, `ProviderHealth`,
      `ProviderDiagnostics`, `SanitizationRules`, `WorkflowDefinitions`
      (tenant-authored), `WorkflowInstances` (tenant runs),
      `DomainEvents`, `QueuedTasks`, `EmailOutbox`, `MentorshipSessions`,
      `MentorshipEvents`, `JuniorDevelopers`, `Stories`, `ApiKeys`
      (tenant scope).
- [ ] No `TenantId` column and **no** `HasQueryFilter` calls — tenancy
      is implicit in the connection string per Doc 01 §1.4.
- [ ] The `ApiKeys` entity on `TenantDbContext` uses a DB-level `CHECK`
      constraint enforcing `Scope = 'tenant'` per Doc 01 §1.4 (the
      constraint itself was added by Story 28-1 migrations; this story
      maps the entity to the constrained column).

### AC2: `ITenantDbContextFactory` contract

- [ ] New interface `ITenantDbContextFactory` under
      `apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantDbContextFactory.cs`:
  ```csharp
  public interface ITenantDbContextFactory
  {
      Task<TenantDbContext> CreateAsync(
          Guid tenantId,
          CancellationToken cancellationToken = default);
  }
  ```
- [ ] Default implementation `TenantDbContextFactory` resolves an
      `NpgsqlDataSource` from the injected
      `ITenantConnectionResolver` (interface declared here, stubbed in
      this story, replaced with real implementation in Story 28-4).
- [ ] DI registration in `Program.cs`:
      `services.AddSingleton<ITenantDbContextFactory,
      TenantDbContextFactory>();`
- [ ] Factory returns a **new** `TenantDbContext` instance per call —
      callers are responsible for `await using` disposal. No pooling at
      the DbContext layer (the Npgsql pool is one layer below).

### AC3: Stub resolver for Story 28-3

- [ ] New interface `ITenantConnectionResolver` (full implementation
      belongs to Story 28-4 and is designed there per Doc 04 §2.1):
  ```csharp
  public interface ITenantConnectionResolver
  {
      NpgsqlDataSource DataSourceFor(Guid tenantId);
      NpgsqlDataSource ElsaDataSourceFor(Guid tenantId);
      ValueTask EvictAsync(Guid tenantId);
      ResolverStats GetStats();
  }
  ```
- [ ] A `StubTenantConnectionResolver` is registered **behind
      `#if DEBUG`** (per `00-sequencing.md` Phase 1 risks) and returns a
      single hardcoded connection string from
      `ConnectionStrings:DefaultTenantForDev` (a dev-only fixture DB —
      `tamma_tenant_dev`) for any `tenantId`.
- [ ] In non-DEBUG builds, DI resolution of `ITenantConnectionResolver`
      throws a descriptive exception if no real implementation is
      registered — prevents the stub from leaking into production.
- [ ] An integration test asserts that a Release-build
      `TenantDbContextFactory.CreateAsync` without a real resolver
      throws `InvalidOperationException` with a message pointing at
      Story 28-4.

### AC4: Fail-fast exceptions

- [ ] New exception types under
      `apps/tamma-elsa/src/Tamma.Data/Exceptions/`:
  - `TenantNotFoundException(Guid tenantId)` — raised when the tenant
    row is missing from CP (per Doc 04 §2.1).
  - `TenantNotProvisionedException(Guid tenantId, TenantStatus status)`
    — raised when `tenants.Status ∉ {active}` (per Doc 04 §2.1 and
    Doc 01 §2.3). Carries the current status for middleware to
    translate into the right 503/410/422 response.
- [ ] The factory's `CreateAsync` propagates both exceptions unchanged;
      it does not wrap them.
- [ ] `TenantNotFoundException` maps to 404; `TenantNotProvisionedException`
      with `Status=provisioning` maps to 503 + `X-Tenant-Status:
      provisioning` (Story 28-8 owns the middleware wiring — this story
      only defines the exception types).

### AC5: `TenantContext` record for log correlation

- [ ] New record `TenantContext(Guid TenantId, string TenantSlug)`
      under `apps/tamma-elsa/src/Tamma.Data/Abstractions/TenantContext.cs`.
- [ ] `ITenantContext` interface exposing `IsResolved`, `TenantId`,
      `TenantSlug` — consumed by Serilog enrichers in the Elsa host
      (per Doc 02 §10.1 — `elsa_instance=tenant:<id>` log field).
- [ ] Request-scoped DI registration:
      `services.AddScoped<ITenantContext, TenantContext>();` (populated
      by `TenantContextMiddleware` in Story 28-8; for this story, a
      manual test harness sets it directly).
- [ ] Attempting to resolve `TenantDbContext` with
      `ITenantContext.IsResolved == false` throws
      `TenantNotResolvedException` at DI time per Doc 04 §3.3.

### AC6: Delete obsolete `TammaDbContext`

- [ ] `TammaDbContext.cs` is deleted from the `Tamma.Data` assembly at
      the end of this story.
- [ ] `ConnectionStrings:DefaultConnection` is removed from
      `appsettings.json` (superseded by
      `ConnectionStrings:ControlPlane` from Story 28-2 and the
      per-tenant resolver from Story 28-4).
- [ ] `docker-compose.yml` no longer creates the legacy `tamma` DB by
      default (a dev flag `CREATE_LEGACY_DB=true` can opt in for
      migration-tooling compatibility, and is documented in the
      deployment runbook).
- [ ] CI `dotnet build` shows zero `[Obsolete]` warnings — if any
      remain, the caller hasn't been migrated and the build fails
      closed.

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/01-control-plane-split.md` §1.2, §1.4
    (tenant-DB shape, no `TenantId`, no query filters).
  - `plans/db-per-tenant/04-connection-pool-and-delete.md` §2.1
    (resolver interface), §3.1–3.3 (DbContext registration, fail-fast
    for pre-tenant endpoints).
  - `plans/db-per-tenant/02-elsa-two-tier.md` §10.1 (log enrichers
    consume `TenantContext`).
- **File layout**:
  - `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` — new.
  - `apps/tamma-elsa/src/Tamma.Data/TenantDbContextFactory.cs` — new.
  - `apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantDbContextFactory.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantConnectionResolver.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantContext.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Exceptions/TenantNotFoundException.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Exceptions/TenantNotProvisionedException.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Exceptions/TenantNotResolvedException.cs`
  - `apps/tamma-elsa/src/Tamma.Data/StubTenantConnectionResolver.cs`
    (DEBUG only).
- **Stub resolver behaviour**: returns a single
  `NpgsqlDataSource` built from
  `ConnectionStrings:DefaultTenantForDev`. Used only in integration
  tests and local dev against a pre-seeded `tamma_tenant_dev` DB. The
  production path lands with Story 28-4.
- **Phase 1 critical path**: Per `00-sequencing.md`, this story closes
  out Phase 1. Deploy gate requires `TenantDbContext` to build against
  the stub resolver and a canary tenant-scoped endpoint round-trips a
  row.

## Dependencies

- **Blocks**: 28-4 (real resolver implementation), 28-5 (workflow
  seeds tenant DB via the factory), 28-8 (middleware consumes the
  factory), 28-9 (refresh rebuilds role from CP, but switch-org
  creates new tokens against target tenant via the factory).
- **Blocked by**: 28-1 (tenant migrations exist), 28-2 (CP DbContext
  established; `TammaDbContext` marked `[Obsolete]`).
- **External**: Npgsql 8+ (`NpgsqlDataSource`), EF Core 8+.

## Test Plan

### Unit tests

- `TenantDbContextFactory.CreateAsync(knownTenantId)` returns a
  disposable `TenantDbContext` whose `Database.GetDbConnection()`
  targets the stub's connection string.
- Factory with an unresolved `ITenantContext` throws
  `TenantNotResolvedException` (per Doc 04 §3.3).
- `TenantNotFoundException` and `TenantNotProvisionedException` carry
  the expected properties for middleware translation.
- Reflection test: `typeof(TenantDbContext).GetProperties()` contains
  exactly the 15 tenant `DbSet<T>` entries from AC1.
- Reflection test: `TammaDbContext` type no longer exists in the
  `Tamma.Data` assembly.

### Integration tests (Testcontainers.PostgreSQL)

- Spin up Postgres with both `tamma_control` and `tamma_tenant_dev`
  DBs. Apply CP + tenant migrations. Round-trip a `DomainEvent` insert
  via `TenantDbContextFactory.CreateAsync(devTenantId)`.
- Assert that writing to `TenantDbContext` with no `TenantId` column
  succeeds and reads back the same row (no implicit query filter
  dropping it).
- Release-build integration test: without registering a real
  `ITenantConnectionResolver`, `CreateAsync` throws
  `InvalidOperationException`. Test runs only in CI's Release config.
- Assert a canary tenant-scoped endpoint (`GET /api/v1/agent-config`
  or similar — pick whatever is already tenant-scoped in the codebase
  today) round-trips successfully via the factory path.

### Manual verification

- Local dev: run the API, hit a tenant-scoped endpoint with a JWT that
  carries the dev-fixture tenant id, confirm a round-trip against
  `tamma_tenant_dev`.
- `dotnet build` in Release config with no real resolver registered
  shows a clear error message pointing at Story 28-4.

## Definition of Done

- [ ] Acceptance criteria all green
- [ ] Unit + integration tests added, suite passes
- [ ] No new CodeQL alerts
- [ ] Design-doc references updated if the impl deviated
- [ ] Reviewed by a second engineer (cross-stream)

## Risks / Open Questions

- **Stub resolver regressions into production.** `00-sequencing.md`
  Phase 1 risks flag this explicitly — mitigation is the `#if DEBUG`
  compile flag plus the Release-build integration test. Verify the
  CI matrix covers both configurations.
- **`TenantDbContext` construction without `ITenantContext`.** The
  fail-fast exception in DI prevents this at runtime, but if a
  handler declares `TenantDbContext` as a constructor arg on a
  CP-only route the failure surface is a startup exception, not a
  compile error. A follow-up Roslyn analyzer (tracked as a separate
  tech-debt ticket) could promote this to a compile-time check.
- **`ITenantDbContextFactory` vs `TenantDbContextPool`.** Design docs
  don't discuss EF Core's `DbContextPool` for `TenantDbContext`.
  Pooling is skipped for now because `TenantDbContext` is created
  per-request and disposal cost is negligible at ≤500 RPS. If
  benchmarks later justify it, pooling can be added behind the
  factory interface. Flagged here so it isn't lost.
