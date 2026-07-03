namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-2 — computes the canonical <c>billing_mode</c> token
/// (<c>"byok"</c> | <c>"platform"</c>) for an LLM call by READING Story 34-3's
/// authoritative mode owner (<c>TenantProviderBilling</c> via
/// <see cref="Pricing.ITenantProviderBillingResolver"/>) and RECONCILING it with
/// Story 32-3's resolved credential source when that is supplied.
///
/// <para>This seam OWNS no mode — it never writes <c>TenantProviderBilling</c>
/// and never reads or returns a key plaintext. It is a pure producer of the tag
/// that the usage DCB event + the <c>ProviderDiagnostic.BillingMode</c> column
/// carry, so Story 35-3 can meter ONLY platform-provided usage.</para>
/// </summary>
public interface IBillingModeTagger
{
    /// <summary>
    /// Resolve the <c>billing_mode</c> token for a call.
    ///
    /// <list type="number">
    ///   <item><description>Read the 34-3 DECLARED mode for
    ///     <c>(tenantId, providerKey)</c>.</description></item>
    ///   <item><description>If <paramref name="credentialSource"/> (Story 32-3's
    ///     <c>ProviderCredential.Source</c>, <c>"byok"</c>/<c>"platform"</c>) is
    ///     supplied and DISAGREES: log WARN, prefer the 32-3 source (it is the
    ///     credential actually used on the wire), and emit ONE
    ///     <c>BILLING.MODE.MISMATCH</c> DCB event.</description></item>
    ///   <item><description>Validate the resulting token is exactly
    ///     <c>byok</c> or <c>platform</c> — anything else is a logged ERROR, not
    ///     a silent tag (AC11).</description></item>
    /// </list>
    ///
    /// Single-user mode resolves to <c>platform</c> semantics with no billable
    /// implication (the <c>NullBillingModeTagger</c> seam).
    /// </summary>
    Task<string> ResolveTagAsync(
        Guid? tenantId,
        string providerKey,
        string? credentialSource = null,
        CancellationToken ct = default);
}
