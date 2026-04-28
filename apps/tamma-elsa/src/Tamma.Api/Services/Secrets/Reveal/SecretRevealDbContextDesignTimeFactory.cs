using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tamma.Api.Services.Secrets.Reveal;

/// <summary>
/// Design-time factory used by
/// <c>dotnet ef migrations add -c SecretRevealDbContext</c>. Migrations
/// are generated from the model graph; the placeholder Postgres
/// connection string is never connected to. Production runtime
/// registration lives in
/// <see cref="Tamma.Api.Extensions.SecretRevealServiceCollectionExtensions.AddTammaSecretReveal"/>.
///
/// <para>Migration history table:
/// <c>__SecretRevealMigrationsHistory</c> — separate from 29-2's
/// <c>__SecretStoreMigrationsHistory</c> so the reveal schema rolls
/// forward independently.</para>
/// </summary>
public class SecretRevealDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<SecretRevealDbContext>
{
    public SecretRevealDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__SecretStore")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__ControlPlane")
            ?? "Host=localhost;Port=5432;Database=tamma_control;Username=tamma;Password=tamma";

        var options = new DbContextOptionsBuilder<SecretRevealDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__SecretRevealMigrationsHistory"))
            .Options;

        return new SecretRevealDbContext(options);
    }
}
