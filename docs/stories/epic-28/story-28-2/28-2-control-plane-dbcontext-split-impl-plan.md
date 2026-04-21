# Story 28-2 Implementation Plan — Split `TammaDbContext` into `ControlPlaneDbContext`

**Status**: Planned (2026-04-20)
**Story brief**: [`28-2-control-plane-dbcontext-split.md`](./28-2-control-plane-dbcontext-split.md)
**Epic 28 phase**: A (Foundation — serial)
**Branch**: `feat/story-28-2-control-plane-dbcontext`

---

## 1. Objective

Introduce `ControlPlaneDbContext` that owns exactly the 14 CP-resident
tables — nothing tenant-scoped. Migrate every auth, admin, and
tenant-lifecycle handler to inject the new context. Mark the existing
`TammaDbContext` `[Obsolete]` so Story 28-3 can delete it cleanly
while building `TenantDbContext`. Adds a new
`ConnectionStrings:ControlPlane` key without removing the old
`DefaultConnection` (deleted in 28-3).

## 2. Dependencies

Hard blockers:

- **Story 28-1** — migration set must exist and apply so the new
  context targets a real schema.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | New DbContext with 14 CP `DbSet`s. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Configurations/ControlPlane/*.cs` | Fluent `IEntityTypeConfiguration<T>` per entity (Users, RefreshTokens, …). |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Data.Tests/ControlPlane/ControlPlaneDbContextTests.cs` | Schema parity with migration; query filter absence; index coverage. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/DbContextCallerAudit.cs` | Grep-based test: every file under `Tamma.Api/` injecting the old context must be in the allow-list (empty by end of this story). |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` | Add `[Obsolete(..., error: false)]` attribute; keep class intact for 28-3 deletion. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register `ControlPlaneDbContext` via `AddDbContext<ControlPlaneDbContext>` with `ConnectionStrings:ControlPlane`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Same. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/*.cs` — 11 endpoint files that inject `TammaDbContext` for CP tables | Swap to `ControlPlaneDbContext`. Specifically: `AuthEndpoints`, `AdminEndpoints`, `OrgEndpoints`, `UsersEndpoints`, `ApiKeysEndpoints`, `GitHubEndpoints`, `DashboardEndpoints`, `InvitesEndpoints`, `PasswordResetEndpoints`, `RefreshTokenEndpoints`, `PlansEndpoints`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/*Repository.cs` for CP entities | Same swap. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | Add `ConnectionStrings:ControlPlane`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.Development.json` | Add dev-targeted CP URL. |
| `/home/meywd/tamma/docs/deployment/connection-strings.md` | Document new key. |

## 5. Sequence of changes

### Step 1 — New DbContext + entity configs (4h)

- `ControlPlaneDbContext.cs`:
  - 14 `DbSet<T>` properties.
  - `OnModelCreating` applies all 14 fluent configs.
  - No `HasQueryFilter`.
- Fluent configs port the Doc 01 §1.2 schema per-entity (indexes,
  CHECK constraints, composite unique keys).
- Unit test: `ControlPlaneDbContextTests` asserts schema parity via
  `Database.GenerateCreateScript()` diff against the migration
  SQL from 28-1.
- **Commit**: `feat(db): ControlPlaneDbContext + configurations`.

### Step 2 — DI registration (1h)

- `Program.cs` in `Tamma.Api` + `Tamma.ElsaServer`.
- `ConnectionStrings:ControlPlane` falls back to `DefaultConnection`
  if unset (local dev convenience).
- **Commit**: `feat(api): register ControlPlaneDbContext`.

### Step 3 — Migrate CP endpoint handlers (4h)

- 11 endpoint files: replace `TammaDbContext` with `ControlPlaneDbContext`.
- Run full API test suite after each 3-file batch to catch
  compilation regressions early.
- **Commit (×3 — group of ~4 endpoints each)**: `fix(api): route auth endpoints through ControlPlaneDbContext`, etc.

### Step 4 — Migrate CP repositories (3h)

- CP-entity repositories (users, tenants, tenant_memberships,
  api_keys-CP-scope, etc.): swap to new context.
- Integration tests pass unchanged.
- **Commit**: `fix(repositories): CP entities on ControlPlaneDbContext`.

### Step 5 — Obsolete marker + audit guard (2h)

- `TammaDbContext` annotated `[Obsolete]`.
- `DbContextCallerAudit` test scans for remaining callers — passes
  only if all CP-entity callers are migrated (tenant-entity callers
  still legitimately use the old context; allow-list them).
- **Commit**: `chore(db): obsolete TammaDbContext + audit guard`.

### Step 6 — Config + docs (2h)

- `appsettings*.json`: add `ConnectionStrings:ControlPlane`.
- `docs/deployment/connection-strings.md`: new section.
- **Commit**: `docs(config): ControlPlane connection string`.

## 6. Test strategy

### Unit

- `ControlPlaneDbContextTests`: schema parity with migration,
  absence of `HasQueryFilter`.
- Each fluent config has an `OnModelCreating_Applies*` test.

### Integration

- Every auth/admin integration test from Epic 18 runs unchanged —
  the DI swap is transparent to handler code.
- `DbContextCallerAudit` gates CI.

### Regression

- Full `dotnet test` pass after migrations.

## 7. Rollback plan

- **Revert**: commits are ordered so reverting in reverse restores
  the pre-split state. Each commit compiles and tests pass.
- **Config key**: `ConnectionStrings:ControlPlane` falls back to
  `DefaultConnection`; leaving both keys present in env is safe.
- **Obsolete marker**: warning only — existing callers continue to
  compile. No behaviour change.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. New DbContext + configs | 4 |
| 2. DI registration | 1 |
| 3. Endpoint migration | 4 |
| 4. Repository migration | 3 |
| 5. Obsolete + guard | 2 |
| 6. Config + docs | 2 |
| **Total** | **16** (matches brief target) |

## 9. Open questions

- **Two DbContexts against the same connection string** during the
  transition (28-2 → 28-3): acceptable? Yes — Npgsql pools per
  connection string, so both contexts share a pool. Doc 04 §3.2
  says this is the expected state mid-migration.
- **`platform_events`, `platform_queued_tasks`, `platform_email_outbox`**
  — these are new tables from 28-1. This story maps them onto
  `ControlPlaneDbContext` even though no handler reads them yet
  (28-5 and 28-6 will).
- **Grep-based audit vs. Roslyn analyzer**: brief says "pick
  cheaper". Plan: grep in an xUnit test. Analyzer can come in 19-6
  follow-up if regressions occur.
- **Will two-context apps cause EF migrations confusion?** Yes —
  `dotnet ef database update` needs `-c ControlPlaneDbContext`
  explicitly. Documented in runbook.
