using Microsoft.Extensions.DependencyInjection;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.Secrets.Handlers;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 — registers the rotation saga's ports + the fallback
/// generic-http handler. Story 29-7 + 29-8 layer their specific
/// handlers on top by calling
/// <c>AddKeyedSingleton&lt;IRotationHandler, ...&gt;(&lt;system-key&gt;)</c>.
/// </summary>
public static class SecretRotationServiceCollectionExtensions
{
    public static IServiceCollection AddTammaSecretRotation(this IServiceCollection services)
    {
        services.AddScoped<ISecretRotationGateway, SecretStoreRotationGateway>();
        services.AddSingleton<IRotationHandlerRegistry, KeyedRotationHandlerRegistry>();
        services.AddScoped<IRotationAuditEmitter, RotationAuditEmitter>();
        services.AddScoped<IRetireScheduler, RetireScheduler>();

        // Fallback generic-http handler (AC4). Uses a named HttpClient
        // so CI/operator can tune timeouts via the usual HttpClientFactory
        // knobs.
        services.AddHttpClient<GenericHttpRotationHandler>();
        services.AddKeyedSingleton<IRotationHandler>(
            "generic-http",
            (sp, _) => sp.GetRequiredService<GenericHttpRotationHandler>());

        // Story 29-7: Postgres role rotation handler.
        services.AddSingleton<IPostgresRotationExecutor, NpgsqlPostgresRotationExecutor>();
        services.AddScoped<PostgresRoleRotationHandler>();
        services.AddKeyedScoped<IRotationHandler>(
            "postgres",
            (sp, _) => sp.GetRequiredService<PostgresRoleRotationHandler>());

        return services;
    }
}
