using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Api.Services.Secrets.Reveal;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI registration for the Story 29-3 reveal-once service + sweeper.
/// Layered on top of <see cref="SecretsServiceCollectionExtensions.AddTammaSecrets"/>
/// (which wires the 29-1 abstractions) and optionally
/// <see cref="SecretsServiceCollectionExtensions.AddTammaPostgresSecrets"/>
/// (which wires the 29-2 Postgres backend).
///
/// <list type="bullet">
///   <item><description><see cref="IDbContextFactory{TContext}"/> for
///     <see cref="SecretRevealDbContext"/> — rides on the same
///     connection as the 29-2 <c>SecretsDbContext</c> by default so
///     the reveal-token rows share the same physical database as the
///     secret rows. Own migration history table
///     (<c>__SecretRevealMigrationsHistory</c>) keeps the two schemas
///     independent on the roll-forward path.</description></item>
///   <item><description><see cref="ISecretRevealService"/> →
///     <see cref="SecretRevealService"/> (scoped).</description></item>
///   <item><description><see cref="RevealTokenSweeper"/> as a
///     <see cref="Microsoft.Extensions.Hosting.IHostedService"/>.</description></item>
/// </list>
///
/// <para><see cref="TimeProvider"/> is registered as a singleton via
/// <see cref="TimeProvider.System"/> — tests swap it for a
/// <see cref="System.TimeProvider"/> test double by calling
/// <c>services.RemoveAll&lt;TimeProvider&gt;()</c> + a fresh AddSingleton.</para>
/// </summary>
public static class SecretRevealServiceCollectionExtensions
{
    /// <summary>
    /// Register the reveal-once pipeline. Safe to call multiple times;
    /// the registrations use TryAdd semantics where they can.
    /// </summary>
    /// <param name="services">Target DI container.</param>
    /// <param name="configuration">Configuration — used to resolve the
    /// connection string per the same 3-step lookup chain as
    /// <see cref="SecretsServiceCollectionExtensions.AddTammaPostgresSecrets"/>.</param>
    /// <param name="connectionString">Optional explicit connection
    /// string; when null, the resolution falls through to
    /// <c>ConnectionStrings:SecretStore</c> →
    /// <c>ConnectionStrings:ControlPlane</c>.</param>
    public static IServiceCollection AddTammaSecretReveal(
        this IServiceCollection services,
        IConfiguration configuration,
        string? connectionString = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var resolvedConnectionString = connectionString
            ?? configuration.GetConnectionString("SecretStore")
            ?? configuration.GetConnectionString("ControlPlane")
            ?? throw new InvalidOperationException(
                "AddTammaSecretReveal: no connection string supplied and " +
                "neither ConnectionStrings:SecretStore nor " +
                "ConnectionStrings:ControlPlane is configured.");

        services.AddDbContextFactory<SecretRevealDbContext>(options =>
        {
            options.UseNpgsql(resolvedConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__SecretRevealMigrationsHistory"));
        });

        // Story 29-3 <see cref="SecretRevealService"/> also needs the
        // Story 29-2 <see cref="SecretsDbContext"/> factory — the
        // reveal flow writes parent secret rows + marks versions as
        // active / retired inside a single transaction. Production
        // Program.cs never calls the full
        // <c>AddTammaPostgresSecrets</c>, so registering the factory
        // here is what actually makes the reveal + query surfaces
        // runnable end-to-end. TryAdd so explicit earlier calls win.
        if (services.All(sd => sd.ServiceType != typeof(IDbContextFactory<SecretsDbContext>)))
        {
            services.AddDbContextFactory<SecretsDbContext>(options =>
            {
                options.UseNpgsql(resolvedConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__SecretStoreMigrationsHistory"));
            });
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ISecretRevealService, SecretRevealService>();
        services.AddHostedService<RevealTokenSweeper>();

        // Story 29-4 / 29-5 query + retire surface. Needs the
        // SecretsDbContext factory registered above. Scoped to match.
        services.TryAddScoped<Tamma.Api.Services.Secrets.Query.ISecretQueryService,
            Tamma.Api.Services.Secrets.Query.SecretQueryService>();

        return services;
    }
}
