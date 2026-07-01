namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Legacy v1 seam — resolves the per-request Postgres connection string
/// for a given tenant. Superseded by the unified
/// <c>Tamma.Data.Abstractions.ITenantConnectionResolver</c> +
/// <c>LruPooledTenantConnectionResolver</c>, where every tenant has a
/// <c>t_&lt;hex&gt;</c> schema + per-tenant role + encrypted connection
/// string and placement is owned by the <c>tenant_databases</c> pool.
///
/// <list type="bullet">
///   <item><description><b>Central connection</b>: returns the central
///     connection string unchanged for every tenant where
///     <c>tenants.cranl_database_url_encrypted IS NULL</c>.</description></item>
///   <item><description><b>Per-tenant Cranl DB</b>: tenant has been
///     provisioned via the Cranl backend and has its own Postgres on
///     Cranl. Returns the decrypted Cranl <c>DATABASE_URL</c> so the
///     per-request DbContext binds to that database.</description></item>
/// </list>
///
/// <para><b>Status (audit cranl/004): STUBBED.</b> The interface +
/// default-central impl land here so the seam is in place without
/// rewriting every repository. Wiring this into
/// <see cref="Tamma.Data.TenantDbContext"/> via a fresh
/// <see cref="Microsoft.EntityFrameworkCore.DbContextOptionsBuilder"/>
/// per-request requires plumbing through <c>AddDbContextFactory</c> and
/// migrating the repository constructors to ask for a factory rather
/// than the singleton context — that cascade is the next milestone
/// once a real Cranl-provisioned tenant is available to test against.
/// In the meantime the Cranl background workflow still populates the
/// encrypted column correctly so the eventual switch is a no-op for
/// already-provisioned rows.</para>
/// </summary>
public interface ITenantConnectionResolver
{
    /// <summary>
    /// Resolve the connection string for the tenant. <paramref name="tenantId"/>
    /// is null for system-scope work (background services, migrations) — the
    /// default impl returns the central admin connection in that case.
    /// </summary>
    Task<TenantConnection> ResolveAsync(Guid? tenantId, CancellationToken ct = default);
}

/// <summary>
/// Resolved connection envelope. Carries the connection string + a
/// flag signalling whether the connection is a dedicated per-tenant
/// database or the shared central plane.
/// </summary>
public sealed record TenantConnection(
    string ConnectionString,
    bool IsPerTenantDatabase);

/// <summary>
/// Stub <see cref="ITenantConnectionResolver"/> that always returns the
/// central connection. Replaced once
/// <see cref="Tamma.Data.ITenantDbContextFactory"/> + the per-request
/// routing seam land — see the interface doc-comment for the cascade
/// scope reason. Until then every tenant rides on the shared Postgres.
///
/// <para>This means: provisioned-on-Cranl tenants will have their
/// <c>cranl_database_url_encrypted</c> populated correctly, but the
/// API will still read/write through the central DB until the routing
/// switch is flipped. The provisioning + schema work is correct; the
/// runtime routing is the only deferred piece.</para>
/// </summary>
public sealed class CentralOnlyTenantConnectionResolver : ITenantConnectionResolver
{
    private readonly string _centralConnectionString;

    public CentralOnlyTenantConnectionResolver(IConfiguration configuration)
    {
        _centralConnectionString =
            configuration.GetConnectionString("TammaAppDb")
            ?? configuration.GetConnectionString("TammaDb")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "No Postgres connection string configured. Expected one of: "
                + "ConnectionStrings:TammaAppDb, ConnectionStrings:TammaDb, "
                + "ConnectionStrings:DefaultConnection.");
    }

    public Task<TenantConnection> ResolveAsync(Guid? tenantId, CancellationToken ct = default)
        => Task.FromResult(new TenantConnection(_centralConnectionString, IsPerTenantDatabase: false));
}
