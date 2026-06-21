using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Activities.Security;

namespace Tamma.Activities.LlmCall.Credentials;

/// <summary>
/// Story 32-3 — DI registration of the provider-credential resolver for the
/// standalone Elsa workflow host (<c>Tamma.ElsaServer</c>), which executes
/// <see cref="CallLlmInlineActivity"/> and does NOT reference <c>Tamma.Api</c>.
///
/// <para>Wires <see cref="ConfigPlatformProviderCredentialResolver"/> (the
/// config-backed platform-key resolver) so the activity binds a NON-null
/// <see cref="IProviderCredentialResolver"/>. Without this the activity sent an
/// empty <c>ApiKey</c> — a hard regression to no-auth. The API process keeps its
/// own cabinet-backed <c>DefaultProviderCredentialResolver</c> (BYOK) via
/// <c>AddProviderCredentialResolution()</c>; this is the engine-host
/// counterpart that owns the platform-key path (AC2/AC12).</para>
///
/// <para>The resolver picks up an <c>IEventRepository</c> for audit emission
/// only if one is registered in the host (optional dependency) — the engine
/// host may run without a durable event sink.</para>
/// </summary>
public static class EngineProviderCredentialServiceCollectionExtensions
{
    public static IServiceCollection AddEngineProviderCredentialResolution(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Provider allowlist (shared with the activity's fail-closed guards).
        services.TryAddSingleton<ProviderAllowlist>();

        // Fallback policy — config-driven mode detection (no Tamma.Api dep).
        services.TryAddSingleton<IPlatformFallbackPolicy, ConfigEnginePlatformFallbackPolicy>();

        // The resolver — singleton. IEventRepository is OPTIONAL: the engine
        // host may not register one, so resolve it via GetService and pass
        // through (the resolver tolerates null and simply skips audit emit).
        services.TryAddSingleton<IProviderCredentialResolver>(sp =>
            new ConfigPlatformProviderCredentialResolver(
                sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
                sp.GetRequiredService<IPlatformFallbackPolicy>(),
                sp.GetRequiredService<ProviderAllowlist>(),
                sp.GetRequiredService<
                    Microsoft.Extensions.Logging.ILogger<
                        ConfigPlatformProviderCredentialResolver>>(),
                sp.GetService<Tamma.Data.Repositories.IEventRepository>()));

        return services;
    }
}
