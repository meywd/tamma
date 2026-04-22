using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tamma.Api.Services.Secrets.Postgres;

/// <summary>
/// Design-time factory used by
/// <c>dotnet ef migrations add -c SecretsDbContext</c>. Migrations
/// are generated from the model graph; the placeholder Postgres
/// connection string is never connected to. Production runtime
/// registration lives in
/// <c>Tamma.Api/Extensions/SecretsServiceCollectionExtensions.cs</c>
/// via <see cref="Tamma.Api.Extensions.SecretsServiceCollectionExtensions.AddTammaPostgresSecrets"/>.
///
/// <para>Migration history table:
/// <c>__SecretStoreMigrationsHistory</c> — separate from Epic 28's
/// <c>__ControlPlaneMigrationsHistory</c> +
/// <c>__TenantMigrationsHistory</c> so the secrets schema rolls
/// forward independently. The same migration is applied to BOTH the
/// control-plane database (platform-scope rows) AND each per-tenant
/// database (tenant-scope rows) — discriminator is implicit in the
/// connection.</para>
/// </summary>
public class SecretsDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<SecretsDbContext>
{
    public SecretsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__SecretStore")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__ControlPlane")
            ?? "Host=localhost;Port=5432;Database=tamma_control;Username=tamma;Password=tamma";

        var options = new DbContextOptionsBuilder<SecretsDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__SecretStoreMigrationsHistory"))
            .Options;

        return new SecretsDbContext(options);
    }
}
