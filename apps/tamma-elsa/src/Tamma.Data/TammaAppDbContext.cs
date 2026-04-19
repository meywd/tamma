using Microsoft.EntityFrameworkCore;

namespace Tamma.Data;

/// <summary>
/// App-role <see cref="DbContext"/> that connects to Postgres as the
/// <c>tamma_app</c> role. Shares the entire model graph with
/// <see cref="TammaDbContext"/> — the split is purely at the connection
/// layer so that RLS policies installed by the Phase-2 migration become
/// effective (<c>tamma_app</c> is NOT a superuser and therefore does NOT
/// bypass RLS).
///
/// <para>Registered in DI alongside the admin-role
/// <see cref="TammaDbContext"/> with its own connection string
/// (<c>TammaAppDb</c>) and a <c>TenantContextInterceptor</c> that binds
/// <c>app.current_tenant_id</c> on every connection open. Per-request
/// endpoint handlers should inject this context; cross-tenant admin
/// paths (migrations, background services, platform-admin endpoints)
/// continue to inject <see cref="TammaDbContext"/>.</para>
///
/// <para>Closes port-gap findings orgs/002 and orgs/004 by restoring
/// the TS <c>withTenantContext</c> + RLS enforcement plane in C#.</para>
/// </summary>
public class TammaAppDbContext : TammaDbContext
{
    public TammaAppDbContext(DbContextOptions<TammaAppDbContext> options)
        : base(ToBaseOptions(options))
    {
    }

    public TammaAppDbContext(DbContextOptions<TammaAppDbContext> options, ITenantContext tenantContext)
        : base(ToBaseOptions(options), tenantContext)
    {
    }

    /// <summary>
    /// Flip the base-class filter shape to fail-closed. Per-request
    /// DbContext scopes with no resolved tenant return zero rows from
    /// every tenant-scoped table rather than leaking cross-tenant data.
    /// Combined with the Phase-2 RLS policies + the
    /// <c>TenantContextInterceptor</c> running <c>set_config</c> on
    /// connection open, this is the belt-and-suspenders isolation layer
    /// the TS port had via <c>withTenantContext</c> (finding orgs/004).
    /// </summary>
    protected override bool EnforceTenantFilter => true;

    /// <summary>
    /// Bridges the subclass-typed <see cref="DbContextOptions{T}"/> to the
    /// base-typed options the parent constructor expects. EF Core's DI
    /// resolves options by the concrete type parameter, so each subclass
    /// needs its own option object even though the downstream shape is
    /// identical. We copy the underlying extensions onto a fresh
    /// <see cref="DbContextOptions{TammaDbContext}"/> so the base class
    /// can read them back unchanged.
    /// </summary>
    private static DbContextOptions<TammaDbContext> ToBaseOptions(
        DbContextOptions<TammaAppDbContext> options)
    {
        var builder = new DbContextOptionsBuilder<TammaDbContext>();
        foreach (var ext in options.Extensions)
        {
            ((Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsBuilderInfrastructure)builder)
                .AddOrUpdateExtension(ext);
        }
        return builder.Options;
    }
}
