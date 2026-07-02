using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Audit;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 37-10 — DI registration for the curated sensitive-action EMISSION seam
/// (distinct from Story 37-1's <c>AddTammaAuditProjection</c>, which registers
/// the read-side projector). Single entry-point so Program.cs wires it with one
/// call.
/// </summary>
public static class AuditEmissionServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ISensitiveActionEmitter"/> (scoped — depends on the
    /// scoped <c>IEventRepository</c>) and the <see cref="IApiKeyAuditHeartbeat"/>
    /// throttle (singleton — the per-key/time-bucket state must survive across
    /// requests). Idempotent.
    /// </summary>
    public static IServiceCollection AddTammaSensitiveActionEmitter(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ISensitiveActionEmitter, SensitiveActionEmitter>();
        services.TryAddSingleton<IApiKeyAuditHeartbeat, ApiKeyAuditHeartbeat>();

        return services;
    }
}
