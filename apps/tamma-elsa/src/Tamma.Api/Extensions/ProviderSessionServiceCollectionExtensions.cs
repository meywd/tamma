using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Providers;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI helpers for the provider-session stack (Story 9-4 port). Registers:
/// <list type="bullet">
///   <item><see cref="ISystemClock"/> — idempotent via <c>TryAddSingleton</c>.</item>
///   <item><see cref="ProviderSessionOptions"/> — defaults (30m TTL, 60s sweep).</item>
///   <item><see cref="IProviderClient"/> → <see cref="HttpProviderClient"/> (singleton).</item>
///   <item><see cref="IProviderSessionService"/> → <see cref="ProviderSessionService"/> (singleton so sessions survive request scope).</item>
///   <item><see cref="ProviderSessionCleanupService"/> — hosted eviction loop.</item>
/// </list>
/// Register from <c>Program.cs</c> alongside the other hardening workstreams.
/// </summary>
public static class ProviderSessionServiceCollectionExtensions
{
    /// <summary>
    /// Register provider-session services. Idempotent — safe to call multiple
    /// times (duplicate calls are no-ops thanks to <c>TryAdd*</c>).
    /// </summary>
    public static IServiceCollection AddProviderSessionServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ISystemClock, SystemClock>();
        services.TryAddSingleton(new ProviderSessionOptions());
        services.TryAddSingleton<IProviderPricingService, ProviderPricingService>();
        services.TryAddSingleton<IProviderClient, HttpProviderClient>();
        services.TryAddSingleton<IProviderSessionService, ProviderSessionService>();
        services.AddHostedService<ProviderSessionCleanupService>();
        return services;
    }
}
