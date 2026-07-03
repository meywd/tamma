namespace Tamma.Api.Services.Integrations;

/// <summary>
/// Per-request BYOK→system resolver for the tenant's JIRA credential bundle,
/// modelled on git BYOK's <c>IGitTokenResolver</c> (tenant→system→fail-loud):
/// <list type="number">
///   <item><b>tenant</b> — the tenant's BYOK bundle from the secret cabinet
///     (<c>integration/jira/config</c>, <c>Scope=tenant</c>). Present ⇒ use it.</item>
///   <item><b>system</b> — the process <c>Jira:*</c> config, consulted ONLY in
///     single-user mode (the sole principal's legitimate creds). NEVER in SaaS —
///     a shared platform JIRA credential with no per-tenant scoping is the
///     confused-deputy the fail-loud rule blocks.</item>
///   <item><b>fail-loud</b> — neither resolvable ⇒ <c>null</c> ⇒ the mediation
///     returns the typed <c>JIRA_CREDENTIAL_UNAVAILABLE</c> failure (never a
///     silent platform default).</item>
/// </list>
/// </summary>
public interface IJiraCredentialResolver
{
    /// <summary>
    /// Resolve the JIRA credential for <paramref name="tenantId"/>, or
    /// <c>null</c> when neither the tenant BYOK bundle nor the single-user system
    /// config yields a complete credential.
    /// </summary>
    Task<JiraCredentialResolution?> ResolveAsync(
        Guid? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Evict any cached resolution for <paramref name="tenantId"/> so the next
    /// resolve re-reads the cabinet. Called by the write endpoints after a
    /// set/remove mutation (mirrors the provider resolver's cache invalidation).
    /// </summary>
    void Invalidate(Guid? tenantId);
}
