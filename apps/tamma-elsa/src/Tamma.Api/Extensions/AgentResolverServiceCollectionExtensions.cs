using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Services.Agents;

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
    /// Register <see cref="IAgentResolverService"/> and its default
    /// implementation. The underlying <c>IAgentConfigRepository</c> is
    /// expected to be registered by <c>Tamma.Data.DependencyInjection</c>.
    /// </summary>
    public static IServiceCollection AddAgentResolverServices(this IServiceCollection services)
    {
        services.AddScoped<IAgentResolverService, AgentResolverService>();
        return services;
    }
}
