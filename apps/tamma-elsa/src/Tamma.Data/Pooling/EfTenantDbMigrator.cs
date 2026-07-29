using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// Production <see cref="ITenantDbMigrator"/>. Builds an ad-hoc
/// <see cref="TenantDbContext"/> rooted on the supplied tenant
/// connection string and invokes <see cref="DatabaseFacade.MigrateAsync"/>.
///
/// <para>Why an ad-hoc context: the runtime DI graph wires
/// <c>TenantDbContext</c> through <see cref="ITenantDbContextFactory"/>
/// which resolves the connection from the per-request tenant id — but
/// the migration step happens BEFORE the new tenant is queryable through
/// any of those seams. The simplest path is to build the options manually
/// from the just-generated connection string.</para>
///
/// <para>The Elsa per-tenant migrator is a no-op stub: per-tenant Elsa
/// databases are deferred (see Story 28-5 plan §9 open questions). The
/// workflow still calls into this method so the activity surface is
/// stable when the dedicated Elsa DBs ship.</para>
/// </summary>
public sealed class EfTenantDbMigrator : ITenantDbMigrator, ITenantDataSourceDbMigrator
{
    private readonly ILogger<EfTenantDbMigrator> _logger;

    public EfTenantDbMigrator(ILogger<EfTenantDbMigrator>? logger = null)
    {
        _logger = logger ?? NullLogger<EfTenantDbMigrator>.Instance;
    }

    public async Task MigrateTenantAppAsync(
        string tenantConnectionString,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantConnectionString))
            throw new ArgumentException(
                "tenantConnectionString must be supplied",
                nameof(tenantConnectionString));

        // Unified-tenancy Phase 1: the connection string's Search Path names the
        // tenant's schema. Unqualified DDL in the baseline lands in the first
        // search_path schema; the history table is pinned to the same schema so
        // each tenant tracks its own applied set. No Search Path → public,
        // exactly the pre-Phase-1 behavior.
        var schema = TenantNaming.SchemaFromConnectionString(tenantConnectionString);

        // Pooling=false — ADO.NET connection pools are process-global and
        // keyed by connection string. Every tenant's migration string is
        // unique, so a pooled migration connection strands one idle
        // physical connection per provisioned tenant until the idle-prune
        // timer (~5 min) fires; provision enough tenants in a window and
        // the cluster runs out of connection slots (53300). Migrations are
        // one-shot per provisioning — a non-pooled connection that closes
        // on dispose is the correct lifetime.
        var migrationConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(
            tenantConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(migrationConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory", schema))
            .Options;

        await MigrateCoreAsync(options, schema, ct).ConfigureAwait(false);
    }

    // ── Story 44-1: the data-source flavour (the sweep's path) ──
    //
    // NpgsqlDataSource.ConnectionString strips the password, so a caller
    // holding a resolver-minted data source cannot round-trip through the
    // string-based method above (SASL/SCRAM "No password has been provided").
    // Migrating OVER the data source keeps the credentials where they live.
    // Search Path survives the stripping, so schema derivation is unchanged.
    // The Pooling=false rationale above does not apply here: connections come
    // from the tenant's own long-lived resolver pool, not a one-shot
    // migration-only pool that would otherwise strand a physical connection.

    public Task MigrateTenantAppAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var schema = TenantNaming.SchemaFromConnectionString(dataSource.ConnectionString);
        return MigrateCoreAsync(BuildDataSourceOptions(dataSource, schema), schema, ct);
    }

    public async Task<int> CountPendingMigrationsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var schema = TenantNaming.SchemaFromConnectionString(dataSource.ConnectionString);
        await using var ctx = new TenantDbContext(BuildDataSourceOptions(dataSource, schema));
        // Reads the per-schema history table only; a schema without one (a
        // tenant provisioned before the first sweep) reports the full set.
        var pending = await ctx.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false);
        return pending.Count();
    }

    private static DbContextOptions<TenantDbContext> BuildDataSourceOptions(
        NpgsqlDataSource dataSource, string? schema) =>
        new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(dataSource, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory", schema))
            .Options;

    private async Task MigrateCoreAsync(
        DbContextOptions<TenantDbContext> options, string? schema, CancellationToken ct)
    {
        await using var ctx = new TenantDbContext(options);
        if (schema is not null)
        {
            // Safety net for callers that migrate before the schema exists
            // (Phase 1 harnesses, admin-credentialed paths). Phase 2: the
            // unified pipeline runs migrations AS THE TENANT ROLE, which has
            // no CREATE privilege on the database — and Postgres checks that
            // privilege BEFORE the IF NOT EXISTS bail-out (deliberate, see
            // CreateSchemaCommand in src/backend/commands/schemacmds.c), so a
            // bare CREATE SCHEMA IF NOT EXISTS fails with 42501 even when
            // TenantProvisioningService.CreateSchemaAsync already created the
            // schema. The DO block skips the CREATE entirely when the schema
            // is present, so the privilege is only needed when genuinely
            // creating. Schema name is validated by SchemaFromConnectionString
            // ([a-z_][a-z0-9_]*); Quote defends in depth.
            // Safety relies on SchemaFromConnectionString enforcing
            // ^[a-z_][a-z0-9_]*$ — no quoting metacharacters can reach
            // the literal embedded in the DO block. Quote() is a second
            // layer of defence for the EXECUTE'd CREATE SCHEMA.
            await ctx.Database.ExecuteSqlRawAsync(
                "DO $$ BEGIN "
                + $"IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = '{schema}') THEN "
                + $"EXECUTE 'CREATE SCHEMA {TenantNaming.Quote(schema)}'; "
                + "END IF; END $$;", ct)
                .ConfigureAwait(false);
        }
        // EF's MigrateAsync is idempotent — only pending migrations
        // execute, the rest are no-ops by reading __TenantMigrationsHistory.
        await ctx.Database.MigrateAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "tenant.lifecycle.migrate_app completed migrations={Count}",
            (await ctx.Database.GetAppliedMigrationsAsync(ct)).Count());
    }

    public Task MigrateTenantElsaAsync(
        string tenantConnectionString,
        CancellationToken ct = default)
    {
        // Per-tenant Elsa DB is deferred. The activity surface stays
        // stable; this method becomes real when those migrations are
        // generated (Story 28-5 plan §9 open question — embed Elsa
        // migration assembly hash, fail-fast on drift).
        _logger.LogDebug(
            "tenant.lifecycle.migrate_elsa skipped reason=elsa_db_not_split");
        return Task.CompletedTask;
    }
}
