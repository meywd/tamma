using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tamma.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations add -c TenantDbContext</c>.
/// Migrations are generated from the model graph; the placeholder Postgres
/// connection string is never connected to. Production runtime registration
/// uses <see cref="Abstractions.ITenantDbContextFactory"/> (Story 28-3) +
/// the per-tenant connection resolver (Story 28-4).
/// </summary>
public class TenantDesignTimeDbContextFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__TenantDesignTime")
            ?? "Host=localhost;Port=5432;Database=tamma_tenant_designtime;Username=tamma;Password=tamma";

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;

        return new TenantDbContext(options);
    }
}
