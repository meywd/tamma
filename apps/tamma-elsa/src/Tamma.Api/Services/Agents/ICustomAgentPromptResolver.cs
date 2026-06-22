namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-17 — the CUSTOM/PRIVATE prompt leg of <c>MaterialiseAsync</c>. A
/// custom agent is a private <c>Agent</c> whose differentiator is its OWN
/// embedded prompts (Epic 32 rule 5). This seam resolves the system/role prompt
/// from the agent's own <c>ConfigJson.prompts</c> (<see cref="AgentPromptSet"/>) —
/// NOT from the Epic 27 store.
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
/// <see cref="Tamma.Core.TammaError"/> with <c>Code == "CUSTOM_PROMPT_UNRESOLVED"</c>
/// (symmetric with the persona leg's <c>PROMPT_UNRESOLVED</c>); it NEVER returns
/// an empty/plain prompt and NEVER falls through to the Epic 27 store
/// (<c>feedback_resolution_no_empty_fallback</c>).</para>
/// </summary>
public interface ICustomAgentPromptResolver
{
    /// <summary>
    /// Resolve a custom (private) agent's prompt from the ALREADY-LOADED prompt
    /// set (parsed by the caller from the version it materialised against):
    /// <c>byRoleAction["&lt;role&gt;:&lt;action&gt;"]</c> → <c>system</c> → ERROR.
    /// The caller threads in the same <see cref="AgentPromptSet"/> it parsed from
    /// the loaded version, so the resolver does NO extra repository read — a
    /// concurrent publish/rollback between the branch decision and the prompt read
    /// cannot tear the resolution. Fail-loud (<see cref="Tamma.Core.TammaError"/>,
    /// <c>Code == "CUSTOM_PROMPT_UNRESOLVED"</c>), NEVER empty/plain, NEVER fall
    /// through to the Epic 27 store.
    /// </summary>
    /// <param name="agentId">The custom agent's id (for diagnostics / fail-loud context).</param>
    /// <param name="prompts">The prompt set already parsed from the loaded version.</param>
    /// <param name="role">The normalized role.</param>
    /// <param name="action">The normalized action, or null for the role-system prompt.</param>
    /// <returns>The resolved prompt text (non-empty).</returns>
    Task<string> ResolveAsync(
        Guid agentId, AgentPromptSet prompts, string role, string? action, CancellationToken ct = default);
}
