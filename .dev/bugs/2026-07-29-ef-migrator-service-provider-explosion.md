# EfTenantDbMigrator's data-source path mints one EF internal service provider per tenant — the 21st tenant in a sweep throws, and the poisoning is process-global

**Date**: 2026-07-29
**Status**: ✅ Resolved (2026-07-29) — see "Resolution" below
**Found by**: Adversarial review of Story 44-1 (commit 665f9a2); empirically confirmed with a 25-tenant sweep returning `Failed: 7`

## The defect

`EfTenantDbMigrator.BuildDataSourceOptions` (`apps/tamma-elsa/src/Tamma.Data/Pooling/EfTenantDbMigrator.cs:103-108`,
the Story 44-1 data-source flavour used by `TenantMigrationSweeper`) passed the tenant's
`NpgsqlDataSource` itself into `UseNpgsql(dataSource, ...)`. EF Core makes the data-source
INSTANCE part of its internal service-provider cache key, so every distinct tenant swept
built (and cached) a fresh internal provider. EF's `ManyServiceProvidersCreatedWarning` is
configured to THROW by default once 20 distinct providers exist.

**Consequence**: a 25-tenant sweep completed 20 tenants and returned `Failed: 7` for the rest
(the 21st context creation onward throws; the failures land as per-tenant `failed` rows so the
sweep "succeeds" with a corrupted result). Worse, the 20-provider cap is **process-global**:
once tripped, re-running the sweep in the same process fails for every tenant whose provider
is not already cached — forever, until the process restarts. The string-based
`MigrateTenantAppAsync(string)` path was never affected (a connection string is compared by
value, and per-tenant strings still each mint a provider — but that path only runs once per
tenant at provisioning, never 20+ in one process).

## Why tests missed it

- The existing sweeper suite (`tests/Tamma.Api.Tests/Tracker/TenantMigrationSweeperTests.cs`)
  topped out at **2 tenants per test** — far below the 20-provider threshold, so every test
  stayed inside EF's cap and the explosion was invisible.
- The repository/migration suites each migrate exactly one tenant schema per fixture.
- The codebase already knew the trap — `TenantDbContextFactory.cs:53-66` deliberately borrows
  a `DbConnection` instead of passing the data source, with a comment explaining exactly this
  cache-key explosion — but the migrator's data-source flavour was written fresh in 44-1 and
  did not copy the pattern.

## The fix

Migrate over a **borrowed connection**, mirroring `TenantDbContextFactory`:
`dataSource.CreateConnection()` + `UseNpgsql(connection, ...)` in both
`MigrateTenantAppAsync(NpgsqlDataSource)` and `CountPendingMigrationsAsync`
(`BuildDataSourceOptions` → `BuildConnectionOptions`). A `DbConnection` is connection-level
state, not part of the provider cache key, so all tenants share one cached internal provider.
The connection is disposed deterministically (`await using`) by the migrator — the caller
owns it, `contextOwnsConnection` stays false — returning it to the resolver's pool.
Semantics preserved: the data source's connection string embeds `Search Path`, so the
borrowed connection lands unqualified DDL in the tenant schema, and
`MigrationsHistoryTable("__TenantMigrationsHistory", schema)` keeps the history table pinned
to the tenant schema exactly as before. The warning was NOT suppressed — the provider
explosion itself is gone.

## Resolution (2026-07-29)

- `apps/tamma-elsa/src/Tamma.Data/Pooling/EfTenantDbMigrator.cs` — both data-source entry
  points now borrow a connection; `BuildConnectionOptions` documents the trap and points at
  the `TenantDbContextFactory` precedent.
- Regression test:
  `TenantMigrationSweeperTests.Sweep_over_more_than_twenty_tenants_all_succeed_and_a_rerun_stays_clean`
  — 25 tenants against real Postgres (Testcontainers), asserts `Failed == 0` /
  `Migrated == 25`, then re-runs the sweep in the same process and asserts a clean
  `AlreadyCurrent == 25` (the process-global-poisoning regression).
