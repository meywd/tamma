using Tamma.Api.Services.Diagnostics;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI registration for the diagnostics service and its collaborators.
/// </summary>
/// <remarks>
/// Register in <c>Program.cs</c> (or the composition-root extension) alongside
/// <c>AddTammaData</c>. Registers:
/// <list type="bullet">
///   <item><see cref="IBudgetConfigProvider"/> → <see cref="InMemoryBudgetConfigProvider"/> (singleton)</item>
///   <item><see cref="IDiagnosticsService"/> → <see cref="DiagnosticsService"/> (singleton with an <see cref="IServiceScopeFactory"/>-driven scope for EF access)</item>
/// </list>
/// </remarks>
public static class DiagnosticsServiceCollectionExtensions
{
    /// <summary>
    /// Register diagnostics services. Idempotent by default via
    /// <c>TryAdd*</c>-style guards on the interface keys.
    /// </summary>
    public static IServiceCollection AddDiagnosticsServices(this IServiceCollection services)
    {
        // Resolve IConfiguration through DI so the budget provider can pick up
        // Budget:LimitUsd / Budget:AlertThreshold / Budget:PeriodDays at startup
        // (finding 005). Per-tenant overrides land via SetConfig from the
        // PUT /api/providers/budget/{tenantId} endpoint.
        services.AddSingleton<IBudgetConfigProvider>(sp =>
            new InMemoryBudgetConfigProvider(sp.GetService<IConfiguration>()));
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        return services;
    }
}
