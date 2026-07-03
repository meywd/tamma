namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-2 — the single-user no-op tagger. In single-user mode there is no
/// billing dimension (the sole user owns all usage), so the tagger always yields
/// <c>platform</c> semantics with no billable-mode implication and never emits a
/// mismatch event. Registered by <c>AddBillingModeTagging</c> in single-user
/// mode (the same Null-seam pattern Story 35-1 uses for <c>NullBillingProvider</c>)
/// so request handlers never branch on mode.
/// </summary>
public sealed class NullBillingModeTagger : IBillingModeTagger
{
    /// <inheritdoc />
    public Task<string> ResolveTagAsync(
        Guid? tenantId,
        string providerKey,
        string? credentialSource = null,
        CancellationToken ct = default)
        => Task.FromResult(BillingModeTokens.Platform);
}
