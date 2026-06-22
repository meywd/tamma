using Tamma.Data.Entities;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-17 — the CUSTOM/PRIVATE prompt leg of <c>MaterialiseAsync</c>. A
/// custom agent is a <see cref="AgentVisibility.Private"/> <see cref="Agent"/>
/// whose differentiator is its OWN embedded prompts (Epic 32 rule 5). This seam
/// resolves the system/role prompt from the agent's own
/// <c>ConfigJson.prompts</c> (<see cref="AgentPromptSet"/>) — NOT from the Epic
/// 27 store.
///
/// <para>This is the parallel of 32-15's <see cref="IPersonaPromptResolver"/>
/// (the persona/public leg → Epic 27). The single documented conditional in
/// <c>AgentResolverService.MaterialiseAsync</c> dispatches a private agent with a
/// NON-EMPTY prompts block to this seam, and everything else (public personas,
/// AND private agents with an empty/absent prompts block) to
/// <see cref="IPersonaPromptResolver"/>.</para>
///
/// <para><b>Resolution order:</b> <c>byRoleAction["&lt;role&gt;:&lt;action&gt;"]</c>
/// → <c>system</c> → ERROR. <b>Fail-loud, never empty/plain.</b> When neither a
/// matching role:action template nor a system fallback resolves, it throws
/// <see cref="CustomPromptUnresolvedException"/>; it NEVER returns an empty/plain
/// prompt and NEVER falls through to the Epic 27 store
/// (<c>feedback_resolution_no_empty_fallback</c>).</para>
/// </summary>
public interface ICustomAgentPromptResolver
{
    /// <summary>
    /// Resolve a custom (private) agent's prompt from its own
    /// <c>ConfigJson.prompts</c>: <c>byRoleAction["&lt;role&gt;:&lt;action&gt;"]</c>
    /// → <c>system</c> → ERROR. The agent's ACTIVE version config is read for the
    /// embedded prompt set. Fail-loud
    /// (<see cref="CustomPromptUnresolvedException"/>), NEVER empty/plain, NEVER
    /// fall through to the Epic 27 store.
    /// </summary>
    /// <returns>The resolved prompt text (non-empty).</returns>
    Task<string> ResolveAsync(Agent agent, string role, string? action, CancellationToken ct = default);
}
