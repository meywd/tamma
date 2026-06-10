using Microsoft.EntityFrameworkCore;
using Tamma.Data.Abstractions;

namespace Tamma.Data;

/// <summary>
/// Default <see cref="ISystemStoreDbContextFactory"/> implementation —
/// returns a tenant-less <see cref="TenantDbContext"/> bound to the CENTRAL
/// database's public schema (no <c>Search Path</c>), where the platform-level
/// system-default rows (<c>TenantId IS NULL</c>) live.
///
/// <para>Construction mirrors the shared-connection-string mode the
/// transitional <see cref="TenantDbContextFactory"/> used: the connection
/// string comes from the same <c>appConnectionString ?? adminConnectionString</c>
/// chain in <see cref="DependencyInjection.AddTammaData"/>, and the migrations
/// history table is pinned to <c>__TenantMigrationsHistory</c> in the default
/// (public) schema — the central connection carries no <c>Search Path</c>, so
/// every system row resolves against <c>public</c>.</para>
/// </summary>
public sealed class SystemStoreDbContextFactory : ISystemStoreDbContextFactory
{
    private readonly DbContextOptions<TenantDbContext> _options;

    public SystemStoreDbContextFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                "SystemStoreDbContextFactory requires a non-empty connection string.",
                nameof(connectionString));

        // Options are immutable + thread-safe — build once, share across every
        // created context. Each TenantDbContext still owns its own connection
        // scope (Npgsql pools underneath), so callers `await using` as usual.
        _options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;
    }

    public ValueTask<TenantDbContext> CreateAsync(CancellationToken cancellationToken = default)
        // Tenant-less context (no tenant id) — system rows carry TenantId IS
        // NULL and are selected by explicit predicates, never a query filter.
        => ValueTask.FromResult(new TenantDbContext(_options));
}
