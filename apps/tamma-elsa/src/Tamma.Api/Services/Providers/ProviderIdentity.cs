namespace Tamma.Api.Services.Providers;

/// <summary>
/// Story 34-3 (Fix) — the ONE raw provider-IDENTITY normalization for the BYOK /
/// billing-mode owner path. A provider handle is normalized to
/// <c>Trim().ToLowerInvariant()</c> with <b>NO alias-family reduction</b> — the
/// SAME string <see cref="DefaultProviderCredentialResolver"/> (its private
/// <c>Normalize</c>) and <c>ProviderCredentialEndpoints.NormalizeProvider</c>
/// compute when they build the Epic-29 cabinet slug
/// (<c>provider/&lt;handle&gt;/api-key</c>). Keying the owner row
/// <c>TenantProviderBilling.ProviderKey</c>, the BYOK cabinet slug, AND the
/// billing-mode read ALL on this handle makes the write slug byte-identical to
/// the resolver read.
///
/// <para><b>Why raw identity, not the rate-card family.</b> BYOK is a
/// per-PROVIDER-IDENTITY decision: <c>github-copilot</c> and <c>openai</c> are
/// DIFFERENT keys, different toggles, different owner rows. Collapsing them to a
/// family (as <c>BillingProviderKey.Canonicalize</c> /
/// <see cref="ProviderRateLookup.Aliases"/> do) makes a <c>github-copilot</c>
/// write land under <c>provider/openai/api-key</c> — clobbering the tenant's
/// OpenAI key and flipping the <c>openai</c> owner row — while a <c>gemini</c>
/// write lands under <c>provider/google/api-key</c>, where the resolver (which
/// reads <c>provider/gemini/api-key</c>) never finds it: the tenant's key is
/// silently unused and the call is billed as BYOK on the platform key. Raw
/// identity closes both gaps.</para>
///
/// <para>The rate-card path (<see cref="ProviderRateLookup"/> and the pricing /
/// cost / margin resolvers) KEEPS the alias-family map — that map answers "how
/// much does a call cost", not "whose key is this". This helper is deliberately
/// separate from it and never reduces to a family.</para>
///
/// <para><b>No allowlist gate here.</b> The BYOK toggle surface is BROADER than
/// <c>ProviderAllowlist.DefaultProviders</c>: single-user tenants legitimately
/// BYOK non-allowlisted CLI providers (e.g. <c>claude-code</c>), and the legacy
/// Anthropic proxy tags on the vendor handle <c>anthropic-claude</c>. Unknown /
/// bogus providers are rejected upstream by the SaaS eligibility gate
/// (<c>IProviderAuthLookup</c> → 422), not by collapsing the identity here.</para>
/// </summary>
public static class ProviderIdentity
{
    /// <summary>
    /// Raw provider-identity key: <c>Trim().ToLowerInvariant()</c>, no alias
    /// reduction. Empty / whitespace → <see cref="string.Empty"/>.
    /// </summary>
    public static string Normalize(string? provider) =>
        (provider ?? string.Empty).Trim().ToLowerInvariant();
}
