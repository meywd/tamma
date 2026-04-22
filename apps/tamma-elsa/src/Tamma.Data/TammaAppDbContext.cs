using Microsoft.EntityFrameworkCore;

namespace Tamma.Data;

/// <summary>
/// <b>OBSOLETE.</b> Superseded by <see cref="TenantDbContext"/> +
/// <see cref="ITenantDbContextFactory"/>. Kept during the Wave A.5
/// cleanup commit window for backward compatibility while callers are
/// migrated. Deleted in the final cleanup commit.
/// </summary>
[Obsolete("Use ITenantDbContextFactory.CreateAsync(tenantId) to get a TenantDbContext. Deleted in Wave A.5 final cleanup.", error: false)]
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

    protected override bool EnforceTenantFilter => true;

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
