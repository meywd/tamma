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

        var optionsBuilder = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ControlPlaneMigrationsHistory"));
        // Story 35-1 follow-up — keep `ef migrations has-pending-model-changes`
        // output clean by suppressing the required-navigation/query-filter
        // advisory on the design-time options too (same seam as runtime DI).
        ControlPlaneDbContext.ConfigureControlPlaneWarnings(optionsBuilder);

        return new ControlPlaneDbContext(optionsBuilder.Options);
    }
}
