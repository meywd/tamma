using Microsoft.EntityFrameworkCore;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Data;

/// <summary>
/// Default <see cref="ITenantDbContextFactory"/> implementation.
///
/// <para>Unified-tenancy Phase 3 — resolver-only. The injected
/// <see cref="ITenantConnectionResolver"/> (production:
/// <see cref="LruPooledTenantConnectionResolver"/>) returns the tenant's
/// per-tenant <see cref="Npgsql.NpgsqlDataSource"/> built from the stored
/// encrypted connection string (<c>Search Path=t_&lt;hex&gt;</c>). The
/// transitional shared-connection-string mode (Wave A.5 — every tenant on
/// the central DB, scoped only by the EF query filter) was removed together
/// with <c>StubTenantConnectionResolver</c>. Platform-level system-default
/// rows are reached via <see cref="ISystemStoreDbContextFactory"/> instead.</para>
/// </summary>
public sealed class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly ITenantConnectionResolver _resolver;

    /// <summary>
    /// Construct with an injected <see cref="ITenantConnectionResolver"/>.
    /// The resolver hands back a per-tenant <c>NpgsqlDataSource</c>; pool
    /// lifetime is owned by the resolver.
    /// </summary>
    public TenantDbContextFactory(ITenantConnectionResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    public async ValueTask<TenantDbContext> CreateAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException(
                "Tenant id is required. Use ControlPlaneDbContext for CP data.",
                nameof(tenantId));

        var builder = new DbContextOptionsBuilder<TenantDbContext>();

        var dataSource = await _resolver
            .GetDataSourceAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        // NpgsqlDataSource.ConnectionString may omit the password — fine,
        // the helper only reads the Search Path key.
        var schema = TenantNaming.SchemaFromConnectionString(dataSource.ConnectionString);

        // Phase 3 — hand EF a per-context CONNECTION (borrowed from the
        // resolver's pooled data source), not the data source itself.
        // Passing an NpgsqlDataSource into UseNpgsql makes the data-source
        // instance part of EF's internal service-provider cache key: every
        // distinct tenant pool then builds (and leaks) a fresh internal
        // service provider, and EF throws
        // ManyServiceProvidersCreatedWarning once 20 tenants have been
        // touched in one process. A DbConnection is connection-level state
        // — all tenants share one internal provider. contextOwnsConnection
        // ensures the connection returns to the data source's pool when the
        // context is disposed.
        var connection = dataSource.CreateConnection();
        builder.UseNpgsql(connection, contextOwnsConnection: true, npgsql =>
            npgsql.MigrationsHistoryTable("__TenantMigrationsHistory", schema));

        return new TenantDbContext(builder.Options, tenantId);
    }
}
