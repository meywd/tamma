namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-15 — the calling principal the persona prompt resolves against:
/// exactly one of <see cref="TenantId"/> (SaaS) XOR <see cref="UserId"/>
/// (single-user) is set, mirroring the <c>prompt_overrides</c> /
/// <c>AgentRoleSelection</c> principal model. Derived from
/// <c>ITammaModeProvider</c> + <c>ITenantContext</c> /
/// <see cref="IAgentRegistryService.ResolvePrincipal"/>.
/// </summary>
public readonly record struct Principal(Guid? TenantId, Guid? UserId)
{
    /// <summary>Build a single-user principal (user-keyed Epic 27 lookup).</summary>
    public static Principal ForUser(Guid? userId) => new(null, userId);

    /// <summary>Build a SaaS principal (tenant-keyed Epic 27 lookup).</summary>
    public static Principal ForTenant(Guid? tenantId) => new(tenantId, null);
}

/// <summary>
/// Story 32-15 — the PUBLIC/persona prompt leg. Personas are prompt-free by
/// contract, so a persona's system/role prompt comes from the Epic 27 prompt
/// store keyed <c>(principal, role, action)</c> — NOT from the persona config.
///
/// <para>This is an explicit injectable SEAM (rather than an inline
/// <c>PromptStoreService.Resolve…</c> call inside
/// <c>AgentResolverService.MaterialiseAsync</c>): the public branch of
/// <c>MaterialiseAsync</c> calls this. The parallel private/custom-agent leg is
/// <c>ICustomAgentPromptResolver</c>, owned by Story 32-17.</para>
///
/// <para><b>Fail-loud, never empty/plain.</b> Resolution is tenant → system →
/// ERROR (single-user: user → system → ERROR). A persona run whose
/// <c>(role, action)</c> has no Epic 27 prompt is a hard error
/// (<c>PROMPT_UNRESOLVED</c>), never a silent empty/plain prompt
/// (<c>feedback_resolution_no_empty_fallback</c>).</para>
/// </summary>
public interface IPersonaPromptResolver
{
    /// <summary>
    /// Resolve a persona's system/role prompt from the Epic 27 store keyed
    /// <c>(principal, role, action)</c>. When <paramref name="action"/> is null,
    /// the role-system (identity preamble) prompt is resolved; otherwise the
    /// role+action prompt. Tenant → system → ERROR; fail-loud, NEVER empty/plain.
    /// </summary>
    /// <returns>The resolved prompt text (non-empty).</returns>
    Task<string> ResolveAsync(
        Principal principal, string role, string? action, CancellationToken ct = default);
}
