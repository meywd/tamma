using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Agents;
using Tamma.Data.Repositories;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI registration for the agent resolver stack
/// (<see cref="IAgentResolverService"/> and its collaborators).
///
/// Registered by the parent application (<c>Program.cs</c>) — this extension
/// deliberately lives outside <c>DependencyInjection.cs</c> to keep the
/// agent-resolver concern self-contained.
/// </summary>
public static class AgentResolverServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IAgentResolverService"/> + the Story 32-2
    /// <see cref="IAgentRegistryService"/>. The underlying repositories
    /// (<c>IAgentConfigRepository</c>, <c>IAgentRepository</c>,
    /// <c>IAgentSelectionRepository</c>, <c>IEventRepository</c>) are registered
    /// by <c>Tamma.Data.DependencyInjection</c>; mode/tenant/http-context come
    /// from <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddAgentResolverServices(this IServiceCollection services)
    {
        services.AddScoped<IAgentRegistryService, AgentRegistryService>();

        // Use the Story 32-2 full constructor so the entity-aware resolve
        // methods have their collaborators. The missing-config recorder is
        // optional (the epic may not be merged) — resolved as null if unregistered.
        services.AddScoped<IAgentResolverService>(sp => new AgentResolverService(
            sp.GetRequiredService<IAgentConfigRepository>(),
            sp.GetService<IConfiguration>(),
            sp.GetRequiredService<ILogger<AgentResolverService>>(),
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IAgentRepository>(),
            sp.GetRequiredService<IEventRepository>(),
            sp.GetService<IMissingConfigRecorder>()));
        return services;
    }
}
