using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Data.Repositories;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 32-3 — DI registration for the BYOK→platform provider-credential
/// resolver, its cache invalidator, and the BYOK read seam. Only wires the
/// cabinet-backed BYOK reader when the Story 29-2 <see cref="SecretsDbContext"/>
/// factory is present (production / secrets-enabled tests); otherwise a Null
/// reader is registered so the resolver degrades to the platform path without
/// a DI-validation failure on hosts with no secret store.
///
/// <para>The <see cref="IProviderCredentialResolver"/> is a singleton so its
/// in-process BYOK cache survives across requests (TTL + explicit invalidate
/// keep it coherent). It depends on the singleton
/// <see cref="Tamma.Api.Services.Secrets.Stopgap.IRuntimeSecretResolver"/>
/// (platform key path) — resolved as optional so single-user / no-secrets
/// hosts still build.</para>
/// </summary>
public static class ProviderCredentialServiceCollectionExtensions
{
    public static IServiceCollection AddProviderCredentialResolution(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Provider allowlist (shared with the activity's fail-closed guards).
        services.TryAddSingleton<ProviderAllowlist>();

        // Fallback policy (mode + config driven).
        services.TryAddSingleton<IPlatformFallbackPolicy, ConfigPlatformFallbackPolicy>();

        // BYOK read seam. Cabinet-backed when the secrets DbContext factory is
        // wired; a Null reader otherwise so the resolver degrades cleanly.
        if (services.Any(d => d.ServiceType
                == typeof(IDbContextFactory<SecretsDbContext>)))
        {
            services.TryAddSingleton<ITenantProviderKeyReader, CabinetTenantProviderKeyReader>();
        }
        else
        {
            services.TryAddSingleton<ITenantProviderKeyReader, NullTenantProviderKeyReader>();
        }

        // The resolver — singleton so the BYOK cache is process-wide. Factory
        // shape so IRuntimeSecretResolver (the platform-key leg) is OPTIONAL:
        // single-user / no-secrets hosts may not have it registered, and the
        // resolver tolerates null (degrading the platform leg to "unset").
        services.TryAddSingleton<IProviderCredentialResolver>(sp =>
            new DefaultProviderCredentialResolver(
                sp.GetRequiredService<ITenantProviderKeyReader>(),
                sp.GetService<IRuntimeSecretResolver>(),
                sp.GetRequiredService<IPlatformFallbackPolicy>(),
                sp.GetRequiredService<IEventRepository>(),
                sp.GetRequiredService<ITammaModeProvider>(),
                sp.GetRequiredService<ProviderAllowlist>(),
                sp.GetRequiredService<ILogger<DefaultProviderCredentialResolver>>(),
                sp.GetService<TimeProvider>()));

        // Cache invalidator (SECRET.ROTATE.ACTIVATED handler + mutation hook).
        services.TryAddSingleton<ProviderCredentialCacheInvalidator>();

        return services;
    }
}
