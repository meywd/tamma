using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
public sealed class EfTenantDbMigrator : ITenantDbMigrator
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

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(tenantConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;

        await using var ctx = new TenantDbContext(options);
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
