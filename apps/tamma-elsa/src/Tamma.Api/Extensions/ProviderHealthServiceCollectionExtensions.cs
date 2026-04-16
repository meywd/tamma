using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Providers;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI registration helpers for the circuit-breaker + chain-resolver stack.
/// <see cref="AddProviderHealthServices"/> registers production services;
/// tests can replace <see cref="ISystemClock"/> after-the-fact.
/// </summary>
public static class ProviderHealthServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ISystemClock"/>, <see cref="CircuitBreakerOptions"/>,
    /// <see cref="ICircuitBreakerService"/>, and <see cref="IProviderChainResolver"/>.
    /// Parent composition root must call this from <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddProviderHealthServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ISystemClock, SystemClock>();
        services.TryAddSingleton(new CircuitBreakerOptions());
        services.AddScoped<ICircuitBreakerService, CircuitBreakerService>();
        services.AddScoped<IProviderChainResolver, ProviderChainResolver>();
        return services;
    }

    /// <summary>
    /// Maps the new <c>POST /api/providers/chain/resolve</c> endpoint. Parent
    /// Program.cs is responsible for calling this on its routing group.
    /// </summary>
    public static IEndpointRouteBuilder MapProviderChainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/providers/chain/resolve", Endpoints.ProviderEndpoints.ResolveChain)
           .RequireAuthorization("SettingsView");
        return app;
    }
}
