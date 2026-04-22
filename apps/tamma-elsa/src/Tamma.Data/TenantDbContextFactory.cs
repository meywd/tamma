using Microsoft.EntityFrameworkCore;
using Tamma.Data.Abstractions;

namespace Tamma.Data;

/// <summary>
/// Default <see cref="ITenantDbContextFactory"/>. Asks the
/// <see cref="ITenantConnectionResolver"/> for the per-tenant
/// <c>NpgsqlDataSource</c>, wraps it in a fresh
/// <see cref="TenantDbContext"/>, and returns it for the caller to
/// dispose.
///
/// <para>Story 28-3 wires this implementation against the stub
/// resolver (<see cref="StubTenantConnectionResolver"/>). Story 28-4
/// swaps the resolver to the LRU pool cache without any change here —
/// the factory is connection-source agnostic.</para>
/// </summary>
public sealed class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly ITenantConnectionResolver _resolver;

    public TenantDbContextFactory(ITenantConnectionResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    public async ValueTask<TenantDbContext> CreateAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var dataSource = await _resolver
            .GetDataSourceAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(dataSource, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;

        return new TenantDbContext(options);
    }
}
