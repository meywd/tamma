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
        // (finding 005). Overrides live in the `budget_configs` Postgres table
        // (finding 005 follow-up): the provider writes straight to the DB and
        // keeps a short-TTL in-memory cache to shield the read path.
        services.AddSingleton<IBudgetConfigProvider>(sp =>
            new PostgresBudgetConfigProvider(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetService<IConfiguration>()));
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        return services;
    }

    /// <summary>
    /// Test-only registration that swaps in the pre-persistence in-memory
    /// provider. Integration tests that don't want a Postgres round-trip per
    /// <c>GetConfig</c> call (e.g. isolated endpoint tests) call this
    /// instead of <see cref="AddDiagnosticsServices"/>.
    /// </summary>
    public static IServiceCollection AddInMemoryDiagnosticsServices(this IServiceCollection services)
    {
        services.AddSingleton<IBudgetConfigProvider>(sp =>
            new InMemoryBudgetConfigProvider(sp.GetService<IConfiguration>()));
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        return services;
    }
}
