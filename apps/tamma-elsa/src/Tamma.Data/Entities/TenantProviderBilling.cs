namespace Tamma.Data.Entities;

/// <summary>
/// Story 34-3 — the AUTHORITATIVE per-<c>(tenant, provider)</c> billing-mode
/// owner. This control-plane row is the single source of truth for whether a
/// tenant runs a provider under <c>byok</c> (their own key) or <c>platform</c>
/// (Tamma's key). Reader A (the pricing-mode resolver behind
/// <c>ITenantProviderPricingModeResolver</c>) and the 35-2 billing-mode tagger
/// both READ this row; Story 32-3's runtime credential resolver reports what
/// key physically resolved and is reconciled against this DECLARED intent
/// (disagreement ⇒ <c>BILLING.MODE.MISMATCH</c>, 32-3 wins). NOTE: that reconcile
/// branch is latent today — <c>LlmProxyService</c> calls the tagger with
/// <c>credentialSource: null</c>, so only the DECLARED (34-3) mode is used. Wiring
/// the 32-3 credential source into the proxy is a tracked follow-up.
///
/// <para><b>Default is absence.</b> There is no back-fill row: a
/// <c>(tenant, provider)</c> with no <c>active</c> row — and every single-user
/// null-tenant call — resolves to <see cref="Tamma.Core.Enums.MetricBillingMode.PlatformProvided"/>
/// (the current reality). BYOK is opt-in and only ever the result of an explicit
/// <c>active</c> row with <see cref="Mode"/> = <c>"byok"</c>.</para>
///
/// <para><b>ProviderKey overload:</b> <see cref="ProviderKey"/> here is the RAW
/// provider IDENTITY (<c>ProviderIdentity.Normalize</c> — <c>Trim().ToLowerInvariant()</c>,
/// NO alias-family reduction), e.g. <c>"gemini"</c> / <c>"github-copilot"</c> — NOT the
/// Cranl tenancy backend label and NOT the rate-card family (which would collapse
/// <c>gemini</c>→<c>google</c>, <c>github-copilot</c>→<c>openai</c> and clobber a sibling
/// provider's key). This is the SAME handle Story 32-3's credential resolver reads and
/// the LLM proxy passes, so both the BYOK write (34-3) and the billing-mode read match
/// this row on that raw handle exactly. (The pricing/rate-card path keeps its own
/// alias-family map — that answers "what does a call cost", not "whose key is this".)</para>
/// </summary>
public class TenantProviderBilling
{
    /// <summary>UUIDv7 primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>FK → <see cref="Tenant"/>. Never null (BYOK is a SaaS/tenant concept).</summary>
    public Guid TenantId { get; set; }

    /// <summary>Provider identifier, e.g. <c>"anthropic"</c> / <c>"openai"</c>.</summary>
    public string ProviderKey { get; set; } = null!;

    /// <summary>
    /// The declared billing posture — the lowercase
    /// <see cref="Tamma.Core.Enums.MetricBillingMode"/> token (<c>"platform"</c>
    /// | <c>"byok"</c>). CHECK-constrained to those two values.
    /// </summary>
    public string Mode { get; set; } = "platform";

    /// <summary>
    /// Cabinet secret name backing a <c>byok</c> row (e.g.
    /// <c>provider/anthropic/api-key</c>); null for a <c>platform</c> row. A
    /// CHECK enforces the XOR (byok ⇒ non-null, platform ⇒ null).
    /// </summary>
    public string? SecretName { get; set; }

    /// <summary><c>"active"</c> | <c>"disabled"</c>. One active row per (tenant, provider).</summary>
    public string Status { get; set; } = "active";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>Navigation to the owning tenant (CP-resident).</summary>
    public Tenant? Tenant { get; set; }
}
