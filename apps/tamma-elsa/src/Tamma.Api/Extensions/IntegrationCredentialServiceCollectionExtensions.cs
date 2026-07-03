using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Integrations;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Extensions;

/// <summary>
/// Integration BYOK — DI registration for the per-tenant JIRA + email credential
/// resolvers (the mediation reads them), the credential-bound JIRA HTTP client, and
/// the cabinet write helper (the write endpoints use it).
///
/// <para>The resolvers are singletons (their in-process BYOK cache is process-wide,
/// like <c>DefaultProviderCredentialResolver</c>). They depend on the singleton
/// <c>ITenantProviderKeyReader</c> registered by <c>AddProviderCredentialResolution</c>
/// (call this AFTER it). The cabinet write helper is scoped (it composes the scoped
/// <c>ISecretStore</c> facade) and is only registered when the secret store is wired
/// (the write endpoints are inert without it).</para>
/// </summary>
public static class IntegrationCredentialServiceCollectionExtensions
{
    public static IServiceCollection AddIntegrationCredentialResolution(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Per-request resolvers (tenant BYOK → single-user system config → fail-loud).
        // Singletons so the short-TTL cache is process-wide; Invalidate() keeps it
        // coherent after a write.
        services.TryAddSingleton<IJiraCredentialResolver, JiraCredentialResolver>();
        services.TryAddSingleton<IEmailCredentialResolver, EmailCredentialResolver>();

        // Credential-bound JIRA HTTP client (the JIRA analog of git's client factory).
        services.TryAddSingleton<IJiraApiClient, JiraApiClient>();

        // Governed cabinet write helper (set via ISecretStore facade; remove via the
        // cabinet seam). Only meaningful with the secret store wired.
        if (services.Any(d => d.ServiceType == typeof(IDbContextFactory<SecretsDbContext>)))
        {
            services.TryAddScoped<IIntegrationCredentialCabinet, IntegrationCredentialCabinet>();
        }

        return services;
    }
}
