namespace Tamma.Api.Services.Integrations;

/// <summary>
/// Per-request BYOK→system resolver for the tenant's email transport credential,
/// modelled on git BYOK's <c>IGitTokenResolver</c> (tenant→system→fail-loud):
/// <list type="number">
///   <item><b>tenant</b> — the tenant's BYOK bundle from the secret cabinet
///     (<c>integration/email/config</c>, <c>Scope=tenant</c>). Present ⇒ use it.</item>
///   <item><b>system</b> — the process <c>Email:*</c> config, consulted ONLY in
///     single-user mode (the sole principal owns the sender domain). NEVER in
///     SaaS — sending under a shared platform sender identity with no per-tenant
///     allowlist is the confused-deputy the fail-loud rule blocks.</item>
///   <item><b>fail-loud</b> — neither resolvable ⇒ <c>null</c> ⇒ the mediation
///     returns the typed <c>EMAIL_CREDENTIAL_UNAVAILABLE</c> failure (never a
///     silent platform default).</item>
/// </list>
/// </summary>
public interface IEmailCredentialResolver
{
    /// <summary>
    /// Resolve the email transport credential for <paramref name="tenantId"/>, or
    /// <c>null</c> when neither the tenant BYOK bundle nor the single-user system
    /// config yields a complete credential.
    /// </summary>
    Task<EmailCredentialResolution?> ResolveAsync(
        Guid? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Evict any cached resolution for <paramref name="tenantId"/> so the next
    /// resolve re-reads the cabinet. Called by the write endpoints after a
    /// set/remove mutation.
    /// </summary>
    void Invalidate(Guid? tenantId);
}
