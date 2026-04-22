# Story 28-3 Implementation Plan — `TenantDbContext` Factory

**Status**: Planned (2026-04-20)
**Story brief**: [`28-3-tenant-dbcontext-factory.md`](./28-3-tenant-dbcontext-factory.md)
**Epic 28 phase**: A (Foundation — serial)
**Branch**: `feat/story-28-3-tenant-dbcontext-factory`

---

## 1. Objective

Ship `TenantDbContext` + `ITenantDbContextFactory` + stub
`ITenantConnectionResolver`. Every tenant-scoped handler injects the
factory; Story 28-4 replaces the stub with the real per-tenant
connection pool. Deletes `TammaDbContext` entirely — its CP callers
moved to `ControlPlaneDbContext` in 28-2, its tenant callers move to
`TenantDbContext` here. This story is the seam that closes the
database-per-tenant model at the code level; the connection plumbing
comes next in 28-4.

## 2. Dependencies

Hard blockers:

- **Story 28-1** — tenant schema exists.
- **Story 28-2** — CP context split; `TammaDbContext` [Obsolete].

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | New DbContext with 15 tenant `DbSet`s. No `TenantId`, no `HasQueryFilter`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Configurations/Tenant/*.cs` | Fluent config per tenant entity (agent_configs, prompt_overrides, …). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantDbContextFactory.cs` | `CreateAsync(Guid tenantId, CancellationToken ct)` contract. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantConnectionResolver.cs` | `DataSourceFor(tenantId)` + `ElsaDataSourceFor(tenantId)` + `EvictAsync` + `GetStats`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/TenantDbContextFactory.cs` | Default impl: gets `NpgsqlDataSource` from resolver, constructs `TenantDbContext(options)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/StubTenantConnectionResolver.cs` | `#if DEBUG`-gated stub that returns a single dev DataSource. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Data.Tests/TenantDbContextFactoryTests.cs` | Factory creates fresh instance per call; disposal semantics; stub returns configured DataSource. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` | **Delete** (Obsolete from 28-2). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/*.cs` (tenant-scoped endpoints: AgentsEndpoints, PromptsEndpoints, DiagnosticsEndpoints, SanitizationEndpoints, WorkflowsEndpoints, etc.) | Inject `ITenantDbContextFactory`; resolve `tenantId` from `TenantContext.Current`; `await using var ctx = await factory.CreateAsync(tenantId)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/*Repository.cs` for tenant entities | Accept `TenantDbContext` (not resolver) in ctor; endpoint constructs it once per request via factory and passes down. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | `services.AddSingleton<ITenantDbContextFactory, TenantDbContextFactory>();` `services.AddSingleton<ITenantConnectionResolver, StubTenantConnectionResolver>();` (in DEBUG). In Release, throws at composition time unless 28-4 registered the real resolver. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.Development.json` | Add `ConnectionStrings:DefaultTenantForDev` for stub. |
| `/home/meywd/tamma/docs/deployment/connection-strings.md` | Document dev-only stub. |

## 5. Sequence of changes

### Step 1 — Entity + DbContext shell (4h)

- `TenantDbContext.cs` with 15 tenant `DbSet`s.
- Fluent configs per entity (mirror tenant schema from 28-1).
- Unit test parity with tenant migration SQL.
- **Commit**: `feat(db): TenantDbContext + configurations`.

### Step 2 — Factory + resolver contracts (2h)

- `ITenantDbContextFactory` + default impl.
- `ITenantConnectionResolver` + stub.
- Tests: factory returns new instance each call; disposal works;
  stub returns configured dev DataSource.
- **Commit**: `feat(db): tenant DbContext factory + stub resolver`.

### Step 3 — Endpoint migration (6h)

- For each tenant-scoped endpoint:
  1. Remove `TammaDbContext` inject.
  2. Add `ITenantDbContextFactory factory`.
  3. Replace `ctx.Foo.Where(...)` with `await using var ctx = await factory.CreateAsync(TenantContext.Current.TenantId); ctx.Foo.Where(...)`.
- Group into 3 commits (4-5 endpoints each) to keep review tight.
- **Commits**: `fix(api): tenant endpoints via ITenantDbContextFactory (group N)`.

### Step 4 — Repository migration (3h)

- Tenant-entity repositories accept `TenantDbContext` (passed by
  caller). No factory in the repo itself — keeps repos pure.
- **Commit**: `fix(repositories): tenant repos accept TenantDbContext`.

### Step 5 — DI + delete old context (2h)

- `Program.cs` registrations as above.
- Delete `TammaDbContext.cs`.
- Update any remaining allow-list entries in `DbContextCallerAudit`.
- Build must be clean with no obsolete warnings.
- **Commit**: `chore(db): delete TammaDbContext`.

### Step 6 — Dev docs (1h)

- Document `DefaultTenantForDev` usage + caveat: "this stub routes
  every tenant to the same DB — tests that rely on per-tenant
  isolation must wait for 28-4".
- **Commit**: `docs(db): tenant DbContext factory + dev stub`.

## 6. Test strategy

### Unit

- `TenantDbContextFactoryTests` (6 cases): fresh instance per call,
  disposal, cancellation token propagation, factory uses resolver.
- `StubTenantConnectionResolverTests` (3 cases): returns configured
  DataSource; throws in Release build.

### Integration

- Every tenant-scoped endpoint's existing integration test runs
  unchanged, now against the stub resolver pointing at the dev DB.
  Expect zero behaviour change because the stub routes every tenant
  to the same DB (multi-tenant isolation comes from 28-4).

### Deploy gate

- Per brief: "fresh Postgres 17; stub resolver integration test
  green." Owned by CI.

## 7. Rollback plan

- **Revert**: reverse-order revert restores `TammaDbContext` (it's
  deleted, so revert includes the file). Commits compile
  independently.
- **Functional safety**: the stub routes everything to one DB, so
  no tenant data is misrouted during soak.
- **Non-reversible**: `TammaDbContext` file deletion is the only
  destructive change; revert restores it verbatim.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Entity + DbContext shell | 4 |
| 2. Factory + resolver contracts | 2 |
| 3. Endpoint migration | 6 |
| 4. Repository migration | 3 |
| 5. DI + delete old context | 2 |
| 6. Dev docs | 1 |
| **Total** | **18** |

Brief target 14h; plan comes in 4h higher because endpoint migration
is broader than the brief estimated (handler count was ~8 in plan;
real count is ~15 after Phase-3 Epic 19 work).

## 9. Open questions

- **`NpgsqlDataSource` lifetime**: singleton per tenant (reused
  across requests) or per-scope? Plan: singleton inside the resolver
  (28-4 will implement the pool); factory just fetches and wraps.
  Documented in `Npgsql 8` best practice.
- **`await using` on `TenantDbContext` vs. `IDisposable`** — EF Core
  supports both; plan: prefer `await using` so async disposal drains
  the pool correctly.
- **Stub behaviour in tests**: integration tests use the stub with
  a Testcontainers DB. Tests that want per-tenant isolation have to
  wait for 28-4 — document this gap in the test README.
- **Will the old `TammaDbContext` deletion break third-party
  plugins?** No — no external consumer. Internal package only.
