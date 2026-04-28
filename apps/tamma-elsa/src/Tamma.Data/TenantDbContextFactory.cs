using Microsoft.EntityFrameworkCore;
using Tamma.Data.Abstractions;

namespace Tamma.Data;

/// <summary>
/// Default <see cref="ITenantDbContextFactory"/> implementation.
///
/// <para>Two construction modes share this class:</para>
/// <list type="bullet">
///   <item><description>Wave A.5 transitional form — a single shared
///     Npgsql connection string resolves every tenant against the
///     central DB. The per-tenant scoping is enforced by the EF query
///     filter wired from <see cref="TenantDbContext.TenantId"/>.
///     Used by DI registration in
///     <see cref="DependencyInjection.AddTammaData"/> while the
///     per-tenant pool cache is still being rolled out.</description></item>
///   <item><description>Story 28-4 target form — an injected
///     <see cref="ITenantConnectionResolver"/> returns a per-tenant
///     <see cref="Npgsql.NpgsqlDataSource"/> (LRU pool). Call sites
///     stay identical; only the resolver implementation changes when
///     the pool cache lands in production.</description></item>
/// </list>
/// </summary>
public sealed class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly string? _connectionString;
    private readonly ITenantConnectionResolver? _resolver;

    /// <summary>
    /// Construct with a shared connection string. Wave A.5 transitional
    /// mode — every tenant resolves to the same central DB, EF query
    /// filter supplies the tenant scoping.
    /// </summary>
    public TenantDbContextFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                "TenantDbContextFactory requires a non-empty connection string.",
                nameof(connectionString));
        _connectionString = connectionString;
    }

    /// <summary>
    /// Construct with an injected <see cref="ITenantConnectionResolver"/>.
    /// Story 28-4 form — the resolver hands back a per-tenant
    /// <c>NpgsqlDataSource</c>; pool lifetime is owned by the resolver.
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

        if (_resolver is not null)
        {
            var dataSource = await _resolver
                .GetDataSourceAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
            builder.UseNpgsql(dataSource, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"));
        }
        else
        {
            builder.UseNpgsql(_connectionString!, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"));
        }

        return new TenantDbContext(builder.Options, tenantId);
    }
}
