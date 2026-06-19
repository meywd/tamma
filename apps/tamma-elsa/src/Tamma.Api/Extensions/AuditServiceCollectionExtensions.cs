using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Audit;
using Tamma.Data.Audit;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 37-1 — DI registration for the curated audit projection. Single
/// entry-point so Program.cs wires it with one call (mirrors
/// <c>AddTammaAlertRuleEngine</c>).
/// </summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Register the catalog-driven audit projector, its insert-if-absent
    /// repository, the lag metric, and the background host. Idempotent.
    /// </summary>
    public static IServiceCollection AddTammaAuditProjection(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Projector + repository are stateless — singletons.
        services.TryAddSingleton<IAuditProjector, AuditProjector>();
        services.TryAddSingleton<IAuditRecordRepository, AuditRecordRepository>();

        // Options — tests post-configure; defaults are production-safe
        // (RunOnStartup defaults FALSE so the loop is opt-in).
        services.TryAddSingleton<AuditProjectorOptions>();

        // Self-registering OTel meter (singleton).
        services.TryAddSingleton<AuditProjectionMetrics>();

        // Background host — spawns a per-tick DI scope for the scoped
        // ControlPlaneDbContext / tenant factory dependencies.
        services.AddHostedService<AuditProjectorBackgroundService>();

        return services;
    }
}
