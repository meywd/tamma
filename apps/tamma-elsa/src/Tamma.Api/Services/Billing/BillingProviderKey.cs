using Tamma.Api.Services.Providers;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Fix 2 — the SINGLE canonical provider-key normalization for billing-mode
/// resolution. The LLM proxy resolves under the vendor handle
/// <c>"anthropic-claude"</c>, but the authoritative owner row
/// (<c>TenantProviderBilling</c>) is keyed by the provider FAMILY
/// (<c>"anthropic"</c>). Without a shared canonical form the two never match, so
/// a declared BYOK row is silently ignored and the tenant is billed platform
/// markup on their own key.
///
/// <para>Canonical form = the provider family via the shared
/// <see cref="ProviderRateLookup.Aliases"/> map (<c>anthropic-claude</c>/<c>claude</c>
/// → <c>anthropic</c>, <c>gemini</c> → <c>google</c>, ...) THEN force-lowercased.
/// Reusing <see cref="ProviderRateLookup"/> keeps ONE alias source (no drift with
/// the pricing lookup); the extra lowercase makes the key deterministic regardless
/// of caller casing — closing the case-sensitive-index vs case-insensitive-read gap.</para>
///
/// <para>Applied on BOTH sides of the lookup:
/// <list type="bullet">
///   <item><b>Read path</b> — <c>TenantProviderBillingResolver</c> and
///     <c>BillingModeTagger</c> canonicalize the incoming provider before matching
///     the owner row.</item>
///   <item><b>Write path (future, documented)</b> — a BYOK write endpoint MUST
///     canonicalize with this helper before persisting
///     <c>TenantProviderBilling.ProviderKey</c>, so the stored key is always the
///     lowercase canonical family and the read match is exact.</item>
/// </list></para>
/// </summary>
public static class BillingProviderKey
{
    /// <summary>
    /// Normalize a (possibly vendor-handle / mixed-case) provider key to its
    /// canonical lowercase family key. Empty/whitespace → <see cref="string.Empty"/>.
    /// Unknown keys pass through the alias map untouched, then are lowercased.
    /// </summary>
    public static string Canonicalize(string? provider)
    {
        var trimmed = (provider ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return ProviderRateLookup.Canonicalize(trimmed).ToLowerInvariant();
    }
}
