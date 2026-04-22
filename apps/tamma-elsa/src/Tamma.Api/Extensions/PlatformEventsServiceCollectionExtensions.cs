using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.PlatformEvents;

namespace Tamma.Api.Extensions;

/// <summary>
/// Wires the in-process <see cref="IPlatformEventBus"/> into DI. Registered
/// as a singleton so subscribers added at composition root persist for the
/// process lifetime; per-request publishers resolve the same instance.
///
/// <para>Story 28-6 §AC4 — companion to the platform repositories
/// registered by <c>AddTammaData</c>. Idempotent (uses TryAdd) so adjacent
/// stories or test fixtures may call it multiple times without conflict
/// or re-register a test-double bus by registering it before invoking
/// this method.</para>
/// </summary>
public static class PlatformEventsServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformEventBus(this IServiceCollection services)
    {
        services.TryAddSingleton<IPlatformEventBus, InMemoryPlatformEventBus>();
        return services;
    }
}
