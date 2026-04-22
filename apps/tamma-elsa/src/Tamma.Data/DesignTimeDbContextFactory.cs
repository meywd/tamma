using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tamma.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations add/script</c>. The
/// runtime DI registration (DependencyInjection.cs) wires the real
/// <c>TammaDbContext</c> with a runtime <see cref="ITenantContext"/>; the
/// design-time path only needs the parameterless constructor variant. We
/// use a placeholder Postgres connection string — migrations are generated
/// from the model graph, not the live database.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TammaDbContext>
{
    public TammaDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=tamma_design;Username=tamma;Password=tamma";

        var options = new DbContextOptionsBuilder<TammaDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__TammaMigrationsHistory"))
            .Options;

        return new TammaDbContext(options);
    }
}
