namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-3 — the WRITE side of the authoritative per-<c>(tenant, provider)</c>
/// billing-mode owner (<c>TenantProviderBilling</c>). Enables / disables BYOK and reads
/// the current mode. The read-only <see cref="ITenantProviderBillingResolver"/> (used by
/// the pricing engine + billing-mode tagger) consumes the rows this service writes.
///
/// <para><b>Enable</b> writes the tenant's key into the Epic 29 cabinet (via
/// <see cref="IProviderByokSecretCabinet"/>) under the canonical slug, upserts the ONE
/// active owner row to <c>byok</c> (idempotent — no duplicate active row), invalidates
/// Story 32-3's credential cache, and emits <c>PRICING.BYOK.ENABLED</c>. <b>Disable</b>
/// flips the owner row back to <c>platform</c> (secret ref tombstoned), retires the
/// cabinet secret, invalidates the cache, and emits <c>PRICING.BYOK.DISABLED</c>. The
/// provider key is ALWAYS the RAW provider IDENTITY
/// (<c>ProviderIdentity.Normalize</c> — <c>Trim().ToLowerInvariant()</c>, NO
/// alias-family reduction) for both the owner row and the cabinet slug, so it lines up
/// byte-for-byte with the credential resolver's read for that same handle
/// (<c>github-copilot</c> ≠ <c>openai</c>).</para>
/// </summary>
public interface ITenantProviderBillingService
{
    /// <summary>
    /// Enable BYOK for <c>(tenantId, provider)</c>: store <paramref name="apiKey"/> in
    /// the cabinet + upsert the active owner row to <c>byok</c>. Idempotent (a re-enable
    /// updates the existing active row + rotates the key). Throws
    /// <see cref="ArgumentException"/> on a blank provider / key (fail-loud, no partial
    /// write). NEVER echoes the key.
    /// </summary>
    Task<ByokModeResult> EnableByokAsync(
        Guid tenantId, string provider, string apiKey, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Disable BYOK for <c>(tenantId, provider)</c>: flip the active owner row back to
    /// <c>platform</c> (SecretName tombstoned) + retire the cabinet secret. Idempotent
    /// (a disable with no active byok row is a no-op that still reports <c>platform</c>).
    /// </summary>
    Task<ByokModeResult> DisableByokAsync(
        Guid tenantId, string provider, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>
    /// The current mode for <c>(tenantId, provider)</c> — <c>byok</c> when an active
    /// byok owner row exists, else <c>platform</c> (the safe default). <c>KeySet</c>
    /// reflects whether a BYOK key is configured; the raw key is NEVER returned.
    /// </summary>
    Task<ByokModeResult> GetModeAsync(
        Guid tenantId, string provider, CancellationToken ct = default);

    /// <summary>
    /// Every active owner row for the tenant, projected to <see cref="ByokModeResult"/>
    /// (provider + mode + keySet). Providers with no active row are simply absent
    /// (platform by default); the raw key is NEVER returned.
    /// </summary>
    Task<IReadOnlyList<ByokModeResult>> ListModesAsync(
        Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Reveal-SAFE projection of a <c>(tenant, provider)</c> billing mode. Carries the
/// raw-identity provider key, the mode wire token (<c>byok</c> | <c>platform</c>) and a
/// <c>KeySet</c> flag — NEVER the API key value (Epic 29 reveal-once rule).
/// </summary>
public sealed record ByokModeResult(string Provider, string Mode, bool KeySet);
