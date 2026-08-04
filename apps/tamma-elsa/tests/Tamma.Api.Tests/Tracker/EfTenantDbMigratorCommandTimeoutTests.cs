using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-1 sweep hygiene item 4 (2026-07-30) — migration DDL gets its own,
/// much longer command timeout, and the RUNTIME pool's 30s is untouched.
///
/// <para>The defect: the tenant pool stamps <c>CommandTimeout=30</c> onto every
/// tenant connection string
/// (<see cref="TenantConnectionPoolOptions.CommandTimeoutSeconds"/>, applied in
/// <c>LruPooledTenantConnectionResolver.BuildDataSource</c>). The sweep migrates
/// over connections borrowed from that same pool, so a heavy migration — the
/// 44-1 story itself predicts CHECK-widening on the highest-row-count table —
/// aborted at 30s and landed as a per-tenant <c>failed</c> row indistinguishable
/// from a real breakage, with the tenant apparently stranded mid-migration.</para>
///
/// <para>Both halves are asserted here because a fix that slowed down the
/// request path would be worse than the bug: the long timeout is set at the EF
/// layer on the MIGRATION context's options only, so contexts built by
/// <see cref="TenantDbContextFactory"/> over the same data source still inherit
/// the connection string's 30s.</para>
///
/// <para>No Docker: <c>GetCommandTimeout()</c> reads configured options, it
/// does not open a connection.</para>
/// </summary>
[TestFixture]
public class EfTenantDbMigratorCommandTimeoutTests
{
    private const string PoolStyleConnectionString =
        "Host=localhost;Database=tamma;Username=u;Password=p;"
        + "Search Path=t_deadbeef;CommandTimeout=30";

    [Test]
    public void Migration_over_a_borrowed_connection_uses_the_long_DDL_timeout()
    {
        using var connection = new NpgsqlConnection(PoolStyleConnectionString);

        using var ctx = new TenantDbContext(
            EfTenantDbMigrator.BuildConnectionOptions(connection, "t_deadbeef"));

        ctx.Database.GetCommandTimeout().Should().Be(
            EfTenantDbMigrator.MigrationCommandTimeoutSeconds,
            "migration DDL must not inherit the request-path 30s ceiling");
        EfTenantDbMigrator.MigrationCommandTimeoutSeconds.Should().BeGreaterThan(
            new TenantConnectionPoolOptions().CommandTimeoutSeconds * 10,
            "'much longer', not 'a bit longer' — a table rewrite is minutes, not seconds");
    }

    [Test]
    public void The_pending_count_read_keeps_the_short_request_path_timeout()
    {
        // 2026-07-30 review, Finding 1.2. The 900s ceiling landed on
        // BuildConnectionOptions, which CountPendingMigrationsAsync shares — and
        // that method runs NO DDL: it is the __TenantMigrationsHistory read the
        // DRY RUN does per tenant, 4-way parallel, on the endpoint's new
        // DEFAULT and SYNCHRONOUS path. One wedged tenant database therefore
        // held a bare POST open for up to 15 minutes where it used to fail at
        // 30 seconds. The read path is now short and the DDL path is still long.
        using var connection = new NpgsqlConnection(PoolStyleConnectionString);

        using var read = new TenantDbContext(
            EfTenantDbMigrator.BuildPendingCountOptions(connection, "t_deadbeef"));
        using var ddl = new TenantDbContext(
            EfTenantDbMigrator.BuildConnectionOptions(connection, "t_deadbeef"));

        read.Database.GetCommandTimeout().Should().Be(
            EfTenantDbMigrator.PendingCountCommandTimeoutSeconds,
            "a metadata read on a synchronous admin request path is a request-path query");
        EfTenantDbMigrator.PendingCountCommandTimeoutSeconds.Should().Be(
            new TenantConnectionPoolOptions().CommandTimeoutSeconds,
            "the read deliberately matches the runtime pool's ceiling — it is the same "
            + "kind of work, and matching restores exactly the pre-fix behaviour for it");
        ddl.Database.GetCommandTimeout().Should().Be(
            EfTenantDbMigrator.MigrationCommandTimeoutSeconds,
            "and the fix must not have walked back the DDL ceiling it was meant to raise");
    }

    [Test]
    public void Migration_over_a_connection_string_uses_the_long_DDL_timeout()
    {
        // The provisioning flavour: a slow baseline on a freshly minted schema
        // can exceed 30s just as easily as a sweep can.
        using var ctx = new TenantDbContext(
            EfTenantDbMigrator.BuildStringOptions(PoolStyleConnectionString, "t_deadbeef"));

        ctx.Database.GetCommandTimeout().Should().Be(
            EfTenantDbMigrator.MigrationCommandTimeoutSeconds);
    }

    [Test]
    public async Task The_runtime_tenant_context_still_gets_the_pools_thirty_second_timeout()
    {
        // The regression this pins: raising the migrator's timeout by touching
        // the pool (or the shared connection string) would silently give every
        // request-path query a 15-minute ceiling.
        await using var dataSource = NpgsqlDataSource.Create(PoolStyleConnectionString);
        var factory = new TenantDbContextFactory(new SingleSourceResolver(dataSource));

        await using var ctx = await factory.CreateAsync(Guid.NewGuid());

        ctx.Database.GetCommandTimeout().Should().BeNull(
            "the runtime factory sets NO EF-level timeout — it defers to the connection string");
        new NpgsqlConnectionStringBuilder(ctx.Database.GetDbConnection().ConnectionString)
            .CommandTimeout.Should().Be(30,
                "and the connection string the pool built still says 30s");
        new TenantConnectionPoolOptions().CommandTimeoutSeconds.Should().Be(30,
            "the pool default itself is unchanged by the migration-timeout fix");
    }

    private sealed class SingleSourceResolver(NpgsqlDataSource dataSource) : ITenantConnectionResolver
    {
        public ValueTask<NpgsqlDataSource> GetDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default) => new(dataSource);

        public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(
            Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ITenantConnectionLease> LeaseAsync(
            Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask EvictAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public TenantConnectionPoolStats GetStats() => new(0, 0, 0);
    }
}
