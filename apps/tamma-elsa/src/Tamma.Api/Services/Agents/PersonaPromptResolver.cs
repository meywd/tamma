using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-15 — default <see cref="IPersonaPromptResolver"/> over the Epic 27
/// <see cref="PromptStoreService"/>. Reads the persona's system/role prompt from
/// the prompt store keyed <c>(principal, role, action)</c> and fails loud on a
/// miss (<c>PROMPT_UNRESOLVED</c>) — personas carry no prompts, so a missing
/// Epic 27 prompt is a hard error, never an empty/plain fallback
/// (<c>feedback_resolution_no_empty_fallback</c>).
///
/// <para>Principal routing mirrors the prompt store's two-surface model: a
/// non-null <see cref="Principal.TenantId"/> takes the SaaS (tenant-keyed) path;
/// otherwise the single-user (user-keyed) path. The prompt store's own
/// resolution is already tenant/user → system → <see cref="TammaError"/>; this
/// resolver maps the role-system <c>null</c> (no override AND no shipped system
/// default) to the same fail-loud contract.</para>
/// </summary>
public sealed class PersonaPromptResolver : IPersonaPromptResolver
{
    private readonly PromptStoreService _promptStore;
    private readonly ILogger<PersonaPromptResolver>? _logger;

    public PersonaPromptResolver(
        PromptStoreService promptStore,
        ILogger<PersonaPromptResolver>? logger = null)
    {
        _promptStore = promptStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> ResolveAsync(
        Principal principal, string role, string? action, CancellationToken ct = default)
    {
        _ = ct;
        var isSaaS = principal.TenantId is not null;

        _logger?.LogDebug(
            "agent.persona_prompt.resolve role={Role} action={Action} mode={Mode}",
            role, action ?? "(role-system)", isSaaS ? "saas" : "single-user");

        // action present → role+action prompt (the store is already fail-loud).
        // We take its SystemPrompt half (the persona's system/role identity).
        if (!string.IsNullOrEmpty(action))
        {
            ResolvedPrompt resolved = isSaaS
                ? await _promptStore.ResolveRoleActionForTenantAsync(
                    principal.TenantId!.Value, role, action)
                : await _promptStore.ResolveRoleActionAsync(principal.UserId, role, action);

            var text = resolved.SystemPrompt;
            if (string.IsNullOrWhiteSpace(text))
            {
                // A role+action resolved but its system half is empty — still a
                // miss for a PERSONA (which needs a system/role prompt). Fail loud.
                throw NotResolved(role, action, principal);
            }
            return text;
        }

        // action null → role-system (identity preamble) prompt. The store
        // returns null when there is neither an override NOR a shipped default.
        var roleSystem = isSaaS
            ? await _promptStore.ResolveRoleSystemForTenantAsync(principal.TenantId!.Value, role)
            : await _promptStore.ResolveRoleSystemAsync(principal.UserId, role);

        if (string.IsNullOrWhiteSpace(roleSystem))
        {
            throw NotResolved(role, action, principal);
        }
        return roleSystem;
    }

    private static TammaError NotResolved(string role, string? action, Principal principal)
        => new(
            "PROMPT_UNRESOLVED",
            $"No Epic 27 prompt for (role='{role}', action='{action ?? "(role-system)"}'); "
            + "personas carry no prompts and there is no empty/plain fallback "
            + "(tenant/user → system → error).",
            new Dictionary<string, object?>
            {
                ["role"] = role,
                ["action"] = action,
                ["tenantId"] = principal.TenantId,
                ["userId"] = principal.UserId,
            },
            retryable: false,
            severity: TammaErrorSeverity.High);
}
