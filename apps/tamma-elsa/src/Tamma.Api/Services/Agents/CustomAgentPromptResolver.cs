using Microsoft.Extensions.Logging;
using Tamma.Core;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-17 — default <see cref="ICustomAgentPromptResolver"/>. Resolves a
/// custom (private) agent's system/role prompt from the ALREADY-LOADED
/// <see cref="AgentPromptSet"/> (parsed by the caller from the version it
/// materialised against) in the order
/// <c>byRoleAction["&lt;role&gt;:&lt;action&gt;"]</c> → <c>system</c> → ERROR.
///
/// <para><b>No extra read.</b> The caller threads in the SAME prompt set it
/// parsed from the loaded version, so this resolver never re-fetches the active
/// version — a concurrent publish/rollback between the branch decision and the
/// prompt read cannot tear the resolution.</para>
///
/// <para><b>Fail-loud, never empty/plain.</b> A non-empty prompts block commits
/// the agent to this branch; when neither a matching role:action template nor a
/// system fallback resolves, it throws a <see cref="TammaError"/> with
/// <c>Code == "CUSTOM_PROMPT_UNRESOLVED"</c> (symmetric with the persona leg's
/// <c>PROMPT_UNRESOLVED</c>). It NEVER returns an empty/plain prompt and NEVER
/// consults the Epic 27 store (<c>feedback_resolution_no_empty_fallback</c>).</para>
///
/// <para><b>Content safety:</b> logs only the source label and the
/// <c>&lt;role&gt;:&lt;action&gt;</c> key — NEVER a prompt template body or the
/// raw <c>ConfigJson</c> (extends 32-1's no-raw-ConfigJson rule).</para>
/// </summary>
public sealed class CustomAgentPromptResolver : ICustomAgentPromptResolver
{
    /// <summary>Stable machine-readable error code (32-5 FailureCode).</summary>
    public const string ErrorCode = "CUSTOM_PROMPT_UNRESOLVED";

    private readonly ILogger<CustomAgentPromptResolver>? _logger;

    public CustomAgentPromptResolver(ILogger<CustomAgentPromptResolver>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string> ResolveAsync(
        Guid agentId, AgentPromptSet prompts, string role, string? action, CancellationToken ct = default)
    {
        _ = ct;

        // The caller (MaterialiseAsync) only routes here when the agent is private
        // with a NON-EMPTY prompts block; defend against an empty/absent set being
        // passed anyway — that is still a no-resolve, and on the custom path a
        // no-resolve is a hard error, never a fallback.
        if (prompts.IsEmpty)
        {
            _logger?.LogWarning(
                "agent.custom_prompt.unresolved agentId={AgentId} role={Role} action={Action} "
                + "promptSource=custom-agent reason=empty-or-absent-prompts",
                agentId, role, action ?? "(role-system)");
            throw Unresolved(agentId, role, action);
        }

        var key = $"{role}:{action}";

        // (a) byRoleAction["<role>:<action>"] — only meaningful when an action is
        // present; the role-system (action == null) path never has a key.
        if (!string.IsNullOrEmpty(action) &&
            prompts.ByRoleAction is not null &&
            prompts.ByRoleAction.TryGetValue(key, out var template) &&
            !string.IsNullOrWhiteSpace(template))
        {
            _logger?.LogDebug(
                "agent.custom_prompt.resolved agentId={AgentId} role={Role} action={Action} "
                + "promptSource=custom-agent match=byRoleAction",
                agentId, role, action);
            return Task.FromResult(template);
        }

        // (b) system fallback.
        if (!string.IsNullOrWhiteSpace(prompts.System))
        {
            _logger?.LogDebug(
                "agent.custom_prompt.resolved agentId={AgentId} role={Role} action={Action} "
                + "promptSource=custom-agent match=system",
                agentId, role, action ?? "(role-system)");
            return Task.FromResult(prompts.System!);
        }

        // (c) ERROR — fail loud, never empty/plain, never fall through to Epic 27.
        _logger?.LogWarning(
            "agent.custom_prompt.unresolved agentId={AgentId} role={Role} action={Action} "
            + "promptSource=custom-agent reason=no-match",
            agentId, role, action ?? "(role-system)");
        throw Unresolved(agentId, role, action);
    }

    /// <summary>
    /// The fail-loud signal for the custom (private) prompt branch — a
    /// <see cref="TammaError"/> mirroring the persona leg's <c>PROMPT_UNRESOLVED</c>
    /// (32-5 maps the Code → FailureCode the same way). Carries only the agentId +
    /// the <c>"&lt;role&gt;:&lt;action&gt;"</c> key — NEVER a prompt template body.
    /// </summary>
    private static TammaError Unresolved(Guid agentId, string role, string? action)
    {
        var roleActionKey = $"{role}:{action ?? "(role-system)"}";
        return new TammaError(
            ErrorCode,
            $"Custom agent '{agentId}' carries a non-empty prompts block but resolved "
            + $"neither byRoleAction['{roleActionKey}'] nor a system fallback; there is no "
            + "empty/plain fallback and the Epic 27 store is never consulted on the custom path.",
            new Dictionary<string, object?>
            {
                ["agentId"] = agentId,
                ["role"] = role,
                ["action"] = action,
                ["roleActionKey"] = roleActionKey,
            },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }
}
