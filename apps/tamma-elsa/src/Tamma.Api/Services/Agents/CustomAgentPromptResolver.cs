using Microsoft.Extensions.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-17 — default <see cref="ICustomAgentPromptResolver"/>. Reads a
/// custom (private) agent's ACTIVE version <c>ConfigJson.prompts</c> and resolves
/// the system/role prompt in the order <c>byRoleAction["&lt;role&gt;:&lt;action&gt;"]</c>
/// → <c>system</c> → ERROR.
///
/// <para><b>Fail-loud, never empty/plain.</b> A non-empty prompts block commits
/// the agent to this branch; when neither a matching role:action template nor a
/// system fallback resolves, it throws
/// <see cref="CustomPromptUnresolvedException"/>. It NEVER returns an empty/plain
/// prompt and NEVER consults the Epic 27 store
/// (<c>feedback_resolution_no_empty_fallback</c>).</para>
///
/// <para><b>Content safety:</b> logs only the source label and the
/// <c>&lt;role&gt;:&lt;action&gt;</c> key — NEVER a prompt template body or the
/// raw <c>ConfigJson</c> (extends 32-1's no-raw-ConfigJson rule).</para>
/// </summary>
public sealed class CustomAgentPromptResolver : ICustomAgentPromptResolver
{
    private readonly IAgentRepository _agents;
    private readonly ILogger<CustomAgentPromptResolver>? _logger;

    public CustomAgentPromptResolver(
        IAgentRepository agents,
        ILogger<CustomAgentPromptResolver>? logger = null)
    {
        _agents = agents;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> ResolveAsync(
        Agent agent, string role, string? action, CancellationToken ct = default)
    {
        var version = await _agents.GetActiveVersionAsync(agent.Id, ct);
        var promptSet = AgentPromptSet.TryRead(version?.ConfigJson);

        // The caller (MaterialiseAsync) only routes here when the agent is
        // private with a NON-EMPTY prompts block; defend against a racing
        // version flip leaving an empty/absent set — that is still a no-resolve,
        // and on the custom path a no-resolve is a hard error, never a fallback.
        if (promptSet is null || promptSet.IsEmpty)
        {
            _logger?.LogWarning(
                "agent.custom_prompt.unresolved agentId={AgentId} role={Role} action={Action} "
                + "promptSource=custom-agent reason=empty-or-absent-prompts",
                agent.Id, role, action ?? "(role-system)");
            throw new CustomPromptUnresolvedException(agent.Id, role, action);
        }

        var key = $"{role}:{action}";

        // (a) byRoleAction["<role>:<action>"] — only meaningful when an action is
        // present; the role-system (action == null) path never has a key.
        if (!string.IsNullOrEmpty(action) &&
            promptSet.ByRoleAction is not null &&
            promptSet.ByRoleAction.TryGetValue(key, out var template) &&
            !string.IsNullOrWhiteSpace(template))
        {
            _logger?.LogDebug(
                "agent.custom_prompt.resolved agentId={AgentId} role={Role} action={Action} "
                + "promptSource=custom-agent match=byRoleAction",
                agent.Id, role, action);
            return template;
        }

        // (b) system fallback.
        if (!string.IsNullOrWhiteSpace(promptSet.System))
        {
            _logger?.LogDebug(
                "agent.custom_prompt.resolved agentId={AgentId} role={Role} action={Action} "
                + "promptSource=custom-agent match=system",
                agent.Id, role, action ?? "(role-system)");
            return promptSet.System!;
        }

        // (c) ERROR — fail loud, never empty/plain, never fall through to Epic 27.
        _logger?.LogWarning(
            "agent.custom_prompt.unresolved agentId={AgentId} role={Role} action={Action} "
            + "promptSource=custom-agent reason=no-match",
            agent.Id, role, action ?? "(role-system)");
        throw new CustomPromptUnresolvedException(agent.Id, role, action);
    }
}
