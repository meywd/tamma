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
        // Story 32-15 — bind Tamma:Agents:DefaultPersonaName (default "claude")
        // for the configured-default-persona resolution.
        services.AddOptions<DefaultPersonaOptions>()
            .Configure<IConfiguration>((opts, cfg) =>
                cfg.GetSection(DefaultPersonaOptions.SectionPath).Bind(opts));

        services.AddScoped<IAgentRegistryService, AgentRegistryService>();

        // Story 32-16 — per-tenant agent/persona enablement (catalog membership).
        // ONE implementation backs BOTH the write/admin service AND the read-only
        // reader seam 32-18 injects; register the reader against the same scoped
        // instance so the gate and the API share one source of truth.
        services.AddScoped<TenantAgentEnablementService>();
        services.AddScoped<ITenantAgentEnablementService>(
            sp => sp.GetRequiredService<TenantAgentEnablementService>());
        services.AddScoped<ITenantAgentEnablementReader>(
            sp => sp.GetRequiredService<TenantAgentEnablementService>());

        // Story 32-15 — the persona/public prompt seam over the Epic 27 store.
        services.AddScoped<IPersonaPromptResolver, PersonaPromptResolver>();

        // Story 32-17 — the custom/private prompt seam over the agent's own
        // embedded ConfigJson.prompts (byRoleAction → system → ERROR, fail-loud).
        services.AddScoped<ICustomAgentPromptResolver, CustomAgentPromptResolver>();

        // Use the Story 32-2 full constructor so the entity-aware resolve
        // methods have their collaborators. The missing-config recorder is
        // optional (the epic may not be merged) — resolved as null if unregistered.
        // Story 32-15 wires the persona prompt seam for the public branch;
        // Story 32-17 wires the custom prompt seam for the private/custom branch.
        services.AddScoped<IAgentResolverService>(sp => new AgentResolverService(
            sp.GetRequiredService<IAgentConfigRepository>(),
            sp.GetService<IConfiguration>(),
            sp.GetRequiredService<ILogger<AgentResolverService>>(),
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IAgentRepository>(),
            sp.GetRequiredService<IEventRepository>(),
            sp.GetService<IMissingConfigRecorder>(),
            sp.GetRequiredService<IPersonaPromptResolver>(),
            sp.GetRequiredService<ICustomAgentPromptResolver>()));
        return services;
    }
}
