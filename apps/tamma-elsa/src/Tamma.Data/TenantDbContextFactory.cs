using Microsoft.EntityFrameworkCore;

namespace Tamma.Data;

/// <summary>
/// Default <see cref="ITenantDbContextFactory"/> implementation. Builds
/// a fresh <see cref="TenantDbContext"/> per call using the connection
/// string registered as <c>TammaAppDb</c> (with fallback to the admin
/// connection for dev environments).
///
/// <para>Transitional implementation: every tenant shares the same
/// central Postgres; the context's fixed-tenant query filter enforces
/// scoping at the EF layer. Story 28-4 replaces this with a real
/// <c>ITenantConnectionResolver</c> that returns a per-tenant
/// <c>NpgsqlDataSource</c>. Call sites do not change — they already
/// pass the tenant id explicitly.</para>
/// </summary>
public sealed class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly string _connectionString;

    public TenantDbContextFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                "TenantDbContextFactory requires a non-empty connection string.",
                nameof(connectionString));
        _connectionString = connectionString;
    }

    public Task<TenantDbContext> CreateAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException(
                "Tenant id is required. Use ControlPlaneDbContext for CP data.",
                nameof(tenantId));

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_connectionString, npgsql =>
                // Tenant context never runs migrations — CP context owns
                // the shared migration history table.
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"))
            .Options;

        return Task.FromResult(new TenantDbContext(options, tenantId));
    }
}
