using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-1 AC8/AC9 — the migrate-all-provisioned-tenants sweep. The
/// platform's first fleet-wide tenant-DDL path, so the proof is deliberately
/// thorough: the sweep reaches a tenant provisioned BEFORE the deploy (the
/// whole point — the creation-only call sites never re-visit it); an
/// already-migrated tenant is reported <c>already-current</c> and untouched; a
/// second sweep is a no-op; one failing tenant is a <c>failed</c> row and
/// never aborts the others; and <c>dryRun</c> reports pending counts while
/// applying NOTHING.
///
/// <para>REQUIRES DOCKER to run (CI-verified). One Postgres 17 container:
/// control plane migrated into <c>public</c> (the
/// <c>AuditProjectorIntegrationTests</c> shape), tenants as <c>t_&lt;hex&gt;</c>
/// schemas, a routing <see cref="ITenantConnectionResolver"/> double standing
/// in for the LRU pool (same seam, same connection-string contract).</para>
/// </summary>
[TestFixture]
[Category("Docker")]
public class TenantMigrationSweeperTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tenant_sweep_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        // The sweep enumerates the CP tenants table, so the control plane
        // must exist for real.
        await using var cp = NewCp();
        await cp.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    [SetUp]
    public async Task ClearTenants()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("TRUNCATE TABLE tenants CASCADE;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ControlPlaneDbContext NewCp() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private string CsFor(Guid tenantId) =>
        new NpgsqlConnectionStringBuilder(_cs)
        {
            SearchPath = TenantNaming.SchemaName(tenantId),
        }.ConnectionString;

    private async Task<Guid> RegisterTenantAsync(string name)
    {
        var id = Guid.NewGuid();
        await using var cp = NewCp();
        cp.Tenants.Add(new Tenant
        {
            Id = id,
            Name = name,
            Slug = $"{name}-{id:N}"[..30],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await cp.SaveChangesAsync();
        return id;
    }

    private TenantMigrationSweeper NewSweeper(RoutingResolver resolver) =>
        new(new PlainCpFactory(_cs), resolver, new EfTenantDbMigrator());

    private async Task<long> CountTablesInSchemaAsync(Guid tenantId, params string[] tables)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = ANY(@tables);
            """, conn);
        cmd.Parameters.AddWithValue("schema", TenantNaming.SchemaName(tenantId));
        cmd.Parameters.AddWithValue("tables", tables);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ───────────────────────── The AC9 proof ─────────────────────────

    [Test]
    public async Task Sweep_reaches_a_pre_provisioned_tenant_and_is_idempotent()
    {
        var tenantA = await RegisterTenantAsync("tenant-a");
        var tenantB = await RegisterTenantAsync("tenant-b");

        // Tenant A took the CREATION path (the only path that existed before
        // this story). Tenant B was "provisioned before the deploy" — its
        // schema has never seen the migrator.
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(tenantA));

        await using var resolver = new RoutingResolver();
        resolver.Map(tenantA, CsFor(tenantA)).Map(tenantB, CsFor(tenantB));
        var sweeper = NewSweeper(resolver);

        var result = await sweeper.SweepAsync(dryRun: false);

        result.Total.Should().Be(2);
        result.Failed.Should().Be(0);
        result.Tenants.Single(t => t.TenantId == tenantA).Outcome
            .Should().Be(TenantMigrationSweep.OutcomeAlreadyCurrent,
                "the creation path already applied the full set — the sweep must not re-run it");
        result.Tenants.Single(t => t.TenantId == tenantB).Outcome
            .Should().Be(TenantMigrationSweep.OutcomeMigrated);

        (await CountTablesInSchemaAsync(tenantB, "work_items", "projects", "tracker_preferences"))
            .Should().Be(3, "the pre-provisioned tenant gains the tracker tables");

        // Idempotence: a second sweep is a fleet-wide no-op.
        var again = await sweeper.SweepAsync(dryRun: false);
        again.Migrated.Should().Be(0);
        again.AlreadyCurrent.Should().Be(2);
    }

    [Test]
    public async Task One_failing_tenant_is_a_row_not_an_abort()
    {
        var healthy = await RegisterTenantAsync("healthy");
        var broken = await RegisterTenantAsync("broken");

        await using var resolver = new RoutingResolver();
        // 'broken' has NO mapping — the resolver throws for it, standing in
        // for an unreachable pool member / missing envelope.
        resolver.Map(healthy, CsFor(healthy));
        var sweeper = NewSweeper(resolver);

        var result = await sweeper.SweepAsync(dryRun: false);

        result.Total.Should().Be(2);
        result.Tenants.Single(t => t.TenantId == healthy).Outcome
            .Should().Be(TenantMigrationSweep.OutcomeMigrated,
                "one tenant's failure must never abort the sweep for the rest");
        var failed = result.Tenants.Single(t => t.TenantId == broken);
        failed.Outcome.Should().Be(TenantMigrationSweep.OutcomeFailed);
        failed.Error.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task DryRun_reports_pending_counts_and_applies_nothing()
    {
        var tenant = await RegisterTenantAsync("dry-run");
        await using var resolver = new RoutingResolver();
        resolver.Map(tenant, CsFor(tenant));
        var sweeper = NewSweeper(resolver);

        var result = await sweeper.SweepAsync(dryRun: true);

        var entry = result.Tenants.Single();
        entry.Outcome.Should().Be(TenantMigrationSweep.OutcomePending,
            "the sweep row's error detail was: {0}", entry.Error ?? "<none>");
        entry.PendingBefore.Should().BeGreaterThan(0,
            "an unmigrated schema reports the full migration set as pending");

        (await CountTablesInSchemaAsync(tenant, "work_items", "projects", "__TenantMigrationsHistory"))
            .Should().Be(0, "dryRun must apply NOTHING — no tables, no history");

        // And the real run afterwards applies exactly what dryRun predicted.
        var applied = await sweeper.SweepAsync(dryRun: false);
        applied.Tenants.Single().Outcome.Should().Be(TenantMigrationSweep.OutcomeMigrated);
        applied.Tenants.Single().PendingBefore.Should().Be(entry.PendingBefore);
    }

    [Test]
    public async Task Sweep_over_more_than_twenty_tenants_all_succeed_and_a_rerun_stays_clean()
    {
        // Regression pin for the EF internal-service-provider explosion:
        // passing the NpgsqlDataSource itself into UseNpgsql makes each
        // tenant's data-source INSTANCE part of EF's provider cache key, and
        // EF's ManyServiceProvidersCreatedWarning THROWS at the 21st distinct
        // provider — a 25-tenant sweep failed 7 and, because the cap is
        // process-global, every re-run in the same process failed too. The
        // migrator now hands EF a borrowed connection (one shared provider),
        // so a fleet beyond 20 must sweep clean twice in one process.
        const int count = 25;
        var tenantIds = new List<Guid>(count);
        for (var i = 0; i < count; i++)
            tenantIds.Add(await RegisterTenantAsync($"fleet-{i}"));

        await using var resolver = new RoutingResolver();
        foreach (var id in tenantIds)
            resolver.Map(id, CsFor(id));
        var sweeper = NewSweeper(resolver);

        var result = await sweeper.SweepAsync(dryRun: false);

        result.Total.Should().Be(count);
        result.Failed.Should().Be(0,
            "no tenant may fail on EF's ManyServiceProvidersCreatedWarning past the 20th; "
            + "failures were: {0}",
            string.Join("; ", result.Tenants.Where(t => t.Error is not null)
                .Select(t => $"{t.TenantId}: {t.Error}")));
        result.Migrated.Should().Be(count);

        // The cap is process-global — the original defect made every LATER
        // sweep in the same process fail for uncached tenants forever.
        var again = await sweeper.SweepAsync(dryRun: false);
        again.Failed.Should().Be(0);
        again.AlreadyCurrent.Should().Be(count);
    }

    [Test]
    public async Task A_tenants_own_OperationCanceledException_is_a_failed_row_not_an_abort()
    {
        // An OCE surfaced by ONE tenant's provider/driver stack (an internal
        // timeout, a poisoned pool) while the SWEEP's token is not canceled
        // must be that tenant's failure row — not an abort of Task.WhenAll
        // that defeats per-tenant isolation.
        var healthy1 = await RegisterTenantAsync("oce-healthy-1");
        var poisoned = await RegisterTenantAsync("oce-poisoned");
        var healthy2 = await RegisterTenantAsync("oce-healthy-2");

        await using var resolver = new RoutingResolver();
        resolver.Map(healthy1, CsFor(healthy1))
            .Map(poisoned, CsFor(poisoned))
            .Map(healthy2, CsFor(healthy2));

        var sweeper = new TenantMigrationSweeper(
            new PlainCpFactory(_cs),
            resolver,
            new OcePoisonedMigrator(TenantNaming.SchemaName(poisoned)));

        var result = await sweeper.SweepAsync(dryRun: false);

        result.Total.Should().Be(3);
        result.Failed.Should().Be(1);
        result.Migrated.Should().Be(2,
            "the two healthy tenants must complete despite the sibling's OCE");
        var failed = result.Tenants.Single(t => t.TenantId == poisoned);
        failed.Outcome.Should().Be(TenantMigrationSweep.OutcomeFailed);
        failed.Error.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Soft_deleted_tenants_are_skipped()
    {
        var live = await RegisterTenantAsync("live");
        var deleted = await RegisterTenantAsync("deleted");
        await using (var cp = NewCp())
        {
            var row = await cp.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == deleted);
            row.DeletedAt = DateTime.UtcNow;
            await cp.SaveChangesAsync();
        }

        await using var resolver = new RoutingResolver();
        resolver.Map(live, CsFor(live)).Map(deleted, CsFor(deleted));
        var result = await NewSweeper(resolver).SweepAsync(dryRun: true);

        result.Total.Should().Be(1);
        result.Tenants.Single().TenantId.Should().Be(live);
    }

    // ───────────────────────────── doubles ─────────────────────────────

    /// <summary>
    /// Stands in for <c>LruPooledTenantConnectionResolver</c>: same seam
    /// (<see cref="ITenantConnectionResolver.GetDataSourceAsync"/> whose data
    /// source carries the tenant's search-path connection string), no LRU, no
    /// encryption. An unmapped tenant throws — the unreachable-tenant case.
    /// </summary>
    private sealed class RoutingResolver : ITenantConnectionResolver, IAsyncDisposable
    {
        private readonly ConcurrentDictionary<Guid, NpgsqlDataSource> _sources = new();
        private readonly Dictionary<Guid, string> _map = new();

        public RoutingResolver Map(Guid tenantId, string connectionString)
        {
            _map[tenantId] = connectionString;
            return this;
        }

        public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
        {
            if (!_map.TryGetValue(tenantId, out var cs))
                throw new InvalidOperationException($"Tenant {tenantId} is not reachable (no envelope).");
            return new ValueTask<NpgsqlDataSource>(
                _sources.GetOrAdd(tenantId, _ => NpgsqlDataSource.Create(cs)));
        }

        public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ITenantConnectionLease> LeaseAsync(
            Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask EvictAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public TenantConnectionPoolStats GetStats() => new(0, 0, 0);

        public async ValueTask DisposeAsync()
        {
            foreach (var source in _sources.Values)
                await source.DisposeAsync();
        }
    }

    /// <summary>
    /// Delegates to the real <see cref="EfTenantDbMigrator"/> except for the
    /// tenant whose schema matches <paramref name="poisonedSchema"/>, whose
    /// migration surfaces an <see cref="OperationCanceledException"/> from its
    /// OWN stack (no sweep-token involvement) — the driver-internal-OCE shape.
    /// </summary>
    private sealed class OcePoisonedMigrator(string poisonedSchema) : ITenantDataSourceDbMigrator
    {
        private readonly EfTenantDbMigrator _inner = new();

        public Task MigrateTenantAppAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
        {
            if (SchemaOf(dataSource) == poisonedSchema)
                throw new OperationCanceledException(
                    "provider-internal timeout surfaced as OCE (sweep token NOT canceled)");
            return _inner.MigrateTenantAppAsync(dataSource, ct);
        }

        public Task<int> CountPendingMigrationsAsync(
            NpgsqlDataSource dataSource, CancellationToken ct = default) =>
            _inner.CountPendingMigrationsAsync(dataSource, ct);

        private static string? SchemaOf(NpgsqlDataSource dataSource) =>
            TenantNaming.SchemaFromConnectionString(dataSource.ConnectionString);
    }

    /// <summary>Minimal CP factory over the container's public schema.</summary>
    private sealed class PlainCpFactory(string cs) : IDbContextFactory<ControlPlaneDbContext>
    {
        public ControlPlaneDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(cs).Options);
    }
}
