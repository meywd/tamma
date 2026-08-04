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
    /// <summary>
    /// Command timeout (seconds) for migration DDL — 15 minutes.
    ///
    /// <para>Story 44-1 follow-up (2026-07-30). The pooled runtime connection
    /// string carries <c>CommandTimeout=30</c>
    /// (<c>TenantConnectionPoolOptions.CommandTimeoutSeconds</c>, applied in
    /// <c>LruPooledTenantConnectionResolver.BuildDataSource</c>). That is the
    /// right ceiling for a request-path query and the wrong one for DDL: an
    /// <c>ALTER TABLE ... ADD CONSTRAINT</c> that rewrites or validates the
    /// biggest table in a large tenant blows 30s routinely, and the operator
    /// sees a per-tenant <c>failed</c> row for a migration that was merely
    /// slow — with the tenant apparently stranded mid-migration.</para>
    ///
    /// <para>The fix is scoped so the runtime pool is untouched: the timeout is
    /// set at the EF layer on the options of the contexts that RUN MIGRATION
    /// DDL, so EF stamps it onto migration commands while every other context
    /// built over the same data source (<c>TenantDbContextFactory</c>, which
    /// sets no EF-level timeout) keeps inheriting the connection string's 30s.
    /// <c>EfTenantDbMigratorCommandTimeoutTests</c> pins both halves.</para>
    ///
    /// <para><b>2026-07-30 review correction.</b> This doc previously claimed
    /// the ceiling applied to "the MIGRATION context's options only". It did
    /// not: <see cref="BuildConnectionOptions"/> is shared with
    /// <see cref="CountPendingMigrationsAsync"/>, which runs NO DDL — it is the
    /// <c>__TenantMigrationsHistory</c> read the DRY RUN performs per tenant,
    /// and the dry run is both the default and (unless <c>?async=true</c>)
    /// synchronous. One wedged tenant database therefore pinned a bare
    /// <c>POST /api/admin/tenants/migrate</c> open for up to 15 minutes where it
    /// used to fail at 30 seconds. The read path now takes
    /// <see cref="PendingCountCommandTimeoutSeconds"/> and only the DDL paths
    /// take this one.</para>
    ///
    /// <para>EF migrations are transactional per migration, so a genuine
    /// timeout still rolls that migration back — the longer ceiling removes
    /// spurious failures, it does not create partially-applied schemas.</para>
    /// </summary>
    public const int MigrationCommandTimeoutSeconds = 900;

    /// <summary>
    /// Command timeout (seconds) for the pending-migration COUNT — the metadata
    /// read behind a dry-run sweep. 30s, deliberately identical to the runtime
    /// pool's <c>TenantConnectionPoolOptions.CommandTimeoutSeconds</c>: reading
    /// one small history table is a request-path query in every respect, and it
    /// sits on the endpoint's synchronous default path, where a long ceiling is
    /// not patience but a held-open HTTP request multiplied by the number of
    /// unreachable tenants. A tenant whose database is wedged must surface as a
    /// prompt per-tenant <c>failed</c> row, which is exactly what the dry run is
    /// for.
    /// </summary>
    public const int PendingCountCommandTimeoutSeconds = 30;

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

        await MigrateCoreAsync(
            BuildStringOptions(migrationConnectionString, schema), schema, ct)
            .ConfigureAwait(false);
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

    public async Task MigrateTenantAppAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var schema = TenantNaming.SchemaFromConnectionString(dataSource.ConnectionString);
        // Borrow a connection; dispose deterministically when the migration
        // completes (returns it to the resolver's pool — see the comment on
        // BuildConnectionOptions for why NOT the data source itself).
        await using var connection = dataSource.CreateConnection();
        await MigrateCoreAsync(BuildConnectionOptions(connection, schema), schema, ct)
            .ConfigureAwait(false);
    }

    public async Task<int> CountPendingMigrationsAsync(
        NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var schema = TenantNaming.SchemaFromConnectionString(dataSource.ConnectionString);
        await using var connection = dataSource.CreateConnection();
        // SHORT timeout: this is a metadata read on the dry run's synchronous
        // default path, not DDL. See PendingCountCommandTimeoutSeconds.
        await using var ctx = new TenantDbContext(BuildPendingCountOptions(connection, schema));
        // Reads the per-schema history table only; a schema without one (a
        // tenant provisioned before the first sweep) reports the full set.
        var pending = await ctx.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false);
        return pending.Count();
    }

    // EF is handed a BORROWED CONNECTION, never the NpgsqlDataSource itself.
    // Passing a data source into UseNpgsql makes that data-source INSTANCE
    // part of EF's internal service-provider cache key — every swept tenant
    // then mints (and leaks) a fresh internal provider, and EF's
    // ManyServiceProvidersCreatedWarning THROWS at the 21st distinct provider.
    // The cap is process-global, so one >20-tenant sweep poisons every later
    // sweep in the same process. A DbConnection is connection-level state: all
    // tenants share one cached internal provider. Same fix as
    // TenantDbContextFactory (Tamma.Data/TenantDbContextFactory.cs:53-66);
    // here the CALLER owns/disposes the connection (contextOwnsConnection
    // defaults to false for the DbConnection overload), returning it to the
    // resolver's pool. The connection string embedded in the data source
    // carries Search Path, so the borrowed connection lands unqualified DDL in
    // the tenant schema, and the history table stays pinned to that same
    // schema — semantics identical to the string-based path above.
    //
    // commandTimeoutSeconds defaults to the DDL ceiling because the migration
    // path is the one that must not inherit a request-path timeout; the
    // pending-count READ passes PendingCountCommandTimeoutSeconds explicitly.
    // The default is the dangerous-if-wrong direction only for reads, and there
    // is exactly one read caller — it states its own value.
    internal static DbContextOptions<TenantDbContext> BuildConnectionOptions(
        NpgsqlConnection connection,
        string? schema,
        int commandTimeoutSeconds = MigrationCommandTimeoutSeconds) =>
        new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connection, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory", schema);
                // EF-level only: the data source's own CommandTimeout=30 still
                // governs every non-migration context over the same pool.
                npgsql.CommandTimeout(commandTimeoutSeconds);
            })
            .Options;

    /// <summary>
    /// The pending-migration COUNT's options — same context, same history-table
    /// pinning, but the request-path timeout. Separate seam (rather than an
    /// inline argument) so the two ceilings are individually assertable:
    /// <c>EfTenantDbMigratorCommandTimeoutTests</c> pins that the read path is
    /// short and the DDL paths are long, which is the whole point of the split.
    /// </summary>
    internal static DbContextOptions<TenantDbContext> BuildPendingCountOptions(
        NpgsqlConnection connection, string? schema) =>
        BuildConnectionOptions(connection, schema, PendingCountCommandTimeoutSeconds);

    /// <summary>
    /// The provisioning (connection-string) flavour's options. Same
    /// migration-DDL timeout as <see cref="BuildConnectionOptions"/>: a slow
    /// baseline on a freshly minted schema is just as capable of exceeding 30s
    /// as a sweep is.
    /// </summary>
    internal static DbContextOptions<TenantDbContext> BuildStringOptions(
        string connectionString, string? schema) =>
        new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory", schema);
                npgsql.CommandTimeout(MigrationCommandTimeoutSeconds);
            })
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
