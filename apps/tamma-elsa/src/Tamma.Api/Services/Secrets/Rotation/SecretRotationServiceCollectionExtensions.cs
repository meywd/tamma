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
        // Story 29-6 (review fix) — gateway options (stale-pending TTL).
        // Registered so IOptions<SecretRotationGatewayOptions> resolves;
        // Program.cs binds the SecretRotationGateway config section on top.
        services.AddOptions<SecretRotationGatewayOptions>();
        services.AddScoped<ISecretRotationGateway, SecretStoreRotationGateway>();
        services.AddSingleton<IRotationHandlerRegistry, KeyedRotationHandlerRegistry>();
        services.AddScoped<IRotationAuditEmitter, RotationAuditEmitter>();
        // Story 29-6 AC8 — the single-version retire body shared by the
        // periodic sweeper and the per-task RetireSecretVersionTaskHandler.
        services.AddScoped<IRetireTaskExecutor, RetireTaskExecutor>();
        services.AddScoped<IRetireScheduler, RetireScheduler>();
        // Story 29-6 audit gap #2/#3 — the trigger surface (operator
        // endpoint + scheduled auto-rotation): concurrency guard, fresh
        // correlation id, rotate-secret dispatch, REQUESTED/REJECTED audit.
        services.AddScoped<IRotationTriggerService, RotationTriggerService>();

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

        // Story 29-8: Cranl env-var rotation handler. ICranlApiClient is
        // expected to be registered already by the provisioning side of
        // the Api (AddHttpClient<ICranlApiClient, CranlApiClient>()).
        services.AddScoped<CranlEnvVarRotationHandler>();
        services.AddKeyedScoped<IRotationHandler>(
            "cranl",
            (sp, _) => sp.GetRequiredService<CranlEnvVarRotationHandler>());

        return services;
    }
}
