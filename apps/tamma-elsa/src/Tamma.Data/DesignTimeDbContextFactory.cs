using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tamma.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations add/script</c>.
/// Targets <see cref="ControlPlaneDbContext"/> — the CP context owns the
/// migrations history table on the shared Postgres. Per-tenant contexts
/// do not run migrations; they rely on the CP context having brought
/// the schema up.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ControlPlaneDbContext>
{
    public ControlPlaneDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=tamma_design;Username=tamma;Password=tamma";

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"))
            .Options;

        return new ControlPlaneDbContext(options);
    }
}
