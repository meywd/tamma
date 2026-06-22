namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-17 — the fail-loud signal for the custom (private) agent prompt
/// branch. Thrown by <see cref="ICustomAgentPromptResolver"/> when a non-empty
/// <c>prompts</c> block resolves NEITHER a matching <c>byRoleAction["role:action"]</c>
/// template NOR a <c>system</c> fallback for the requested <c>(role, action)</c>.
///
/// <para>This is the only no-resolve outcome on the custom path: the resolver
/// NEVER returns an empty/plain prompt and NEVER falls through to the Epic 27
/// store (<c>feedback_resolution_no_empty_fallback</c>). The managed
/// <c>call-LLM</c> path (32-5) catches this and maps it to a typed
/// <c>FailureCode = "CUSTOM_PROMPT_UNRESOLVED"</c> on the run result rather than
/// letting a bare exception escape a managed run (AC7).</para>
///
/// <para><b>Content safety:</b> carries only the <see cref="AgentId"/> and the
/// <see cref="RoleActionKey"/> (<c>"&lt;role&gt;:&lt;action&gt;"</c>) — NEVER a
/// prompt template body.</para>
/// </summary>
public sealed class CustomPromptUnresolvedException : Exception
{
    /// <summary>Stable machine-readable error code, surfaced as the 32-5 FailureCode.</summary>
    public const string ErrorCode = "CUSTOM_PROMPT_UNRESOLVED";

    /// <summary>Stable machine-readable error code (always <see cref="ErrorCode"/>).</summary>
    public string Code => ErrorCode;

    /// <summary>The custom agent whose embedded prompts failed to resolve.</summary>
    public Guid AgentId { get; }

    /// <summary>The <c>"&lt;role&gt;:&lt;action&gt;"</c> key that did not resolve (action may be the role-system marker).</summary>
    public string RoleActionKey { get; }

    public CustomPromptUnresolvedException(Guid agentId, string role, string? action)
        : base(
            $"Custom agent '{agentId}' carries a non-empty prompts block but resolved "
            + $"neither byRoleAction['{role}:{action ?? "(role-system)"}'] nor a system "
            + "fallback; there is no empty/plain fallback and the Epic 27 store is never "
            + "consulted on the custom path.")
    {
        AgentId = agentId;
        RoleActionKey = $"{role}:{action ?? "(role-system)"}";
    }
}
