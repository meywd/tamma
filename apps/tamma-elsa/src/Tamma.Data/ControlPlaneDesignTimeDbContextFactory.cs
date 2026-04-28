using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tamma.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations add -c ControlPlaneDbContext</c>.
/// Migrations are generated from the model graph; the placeholder Postgres
/// connection string is never connected to. Production runtime registration
/// lives in <c>Tamma.Api/Program.cs</c> via <see cref="DependencyInjection"/>.
/// </summary>
public class ControlPlaneDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ControlPlaneDbContext>
{
    public ControlPlaneDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ControlPlane")
            ?? "Host=localhost;Port=5432;Database=tamma_control;Username=tamma;Password=tamma";

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ControlPlaneMigrationsHistory"))
            .Options;

        return new ControlPlaneDbContext(options);
    }
}
