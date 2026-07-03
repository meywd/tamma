namespace Tamma.Core.Enums;

/// <summary>
/// Story 34-3 / 35-2 — the single canonical BYOK-vs-platform BILLING POSTURE
/// token, shared by the authoritative owner (<c>TenantProviderBilling</c>),
/// the pricing engine (34-5, via <see cref="PricingMode"/>), the per-call cost
/// record (<c>ProviderDiagnostic.BillingMode</c>), the usage DCB tag
/// (<c>billing_mode</c>) and analytics (36-1, via <see cref="CostBasis"/>).
///
/// <para>Before this story the posture was fragmented across four divergent
/// enums (<see cref="Tamma.Core.Billing.BillingMode"/>,
/// <see cref="PricingMode"/>, <see cref="CostBasis"/> and
/// <c>CredentialSource</c>) with three readers that never agreed.
/// <see cref="MetricBillingMode"/> is the shared vocabulary those readers
/// reconcile to — it mirrors the <c>EntitlementMetricKey</c> single-source rule
/// (Story 34-1) so the posture never drifts between layers.</para>
///
/// <para><b>Wire token.</b> The persisted / tagged form is the lowercase
/// <see cref="MetricBillingModeExtensions.ToToken"/> value (<c>"platform"</c> /
/// <c>"byok"</c>) — the SAME token <c>CredentialSource.ToTag()</c> (Story 32-3)
/// emits and the analytics <c>ResolveCostBasis</c> reader keys off, so a value
/// written here resolves identically on every reader.</para>
/// </summary>
public enum MetricBillingMode
{
    /// <summary>
    /// Platform-provided — Tamma supplies the provider credential and bills the
    /// tenant for metered token usage with the platform markup applied. The
    /// SAFE DEFAULT: a null tenant (single-user) or a tenant with no explicit
    /// owner row resolves here (ordinal 0), so the status quo is preserved.
    /// </summary>
    PlatformProvided = 0,

    /// <summary>
    /// Bring-your-own-key — the tenant supplies their own provider credential.
    /// Only ever the result of an explicit owner row; the token component of the
    /// sell price is 0 (no platform markup).
    /// </summary>
    Byok = 1,
}

/// <summary>
/// Token conversions for <see cref="MetricBillingMode"/>. The lowercase tokens
/// (<c>"platform"</c> / <c>"byok"</c>) are the canonical persisted/tagged form —
/// matching <c>CredentialSource.ToTag()</c> (32-3) and the analytics
/// <c>billing_mode</c> discriminator (36-1).
/// </summary>
public static class MetricBillingModeExtensions
{
    /// <summary>The lowercase wire token for <see cref="MetricBillingMode.PlatformProvided"/>.</summary>
    public const string PlatformToken = "platform";

    /// <summary>The lowercase wire token for <see cref="MetricBillingMode.Byok"/>.</summary>
    public const string ByokToken = "byok";

    /// <summary>Project to the canonical lowercase wire token.</summary>
    public static string ToToken(this MetricBillingMode mode) =>
        mode == MetricBillingMode.Byok ? ByokToken : PlatformToken;

    /// <summary>
    /// Parse a wire token back to the enum. Accepts the lowercase tokens
    /// (<c>"byok"</c> / <c>"platform"</c>, case-insensitive) AND the member
    /// names (<c>"Byok"</c> / <c>"PlatformProvided"</c>) so a
    /// <c>BillingCustomer.BillingMode</c> member-name value round-trips too.
    /// Returns <c>false</c> for anything else — the caller decides whether an
    /// unknown token is a silent default or a fail-loud ERROR (35-2 AC11).
    /// </summary>
    public static bool TryParseToken(string? token, out MetricBillingMode mode)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case ByokToken:
                mode = MetricBillingMode.Byok;
                return true;
            case PlatformToken:
            case "platformprovided":
                mode = MetricBillingMode.PlatformProvided;
                return true;
            default:
                mode = MetricBillingMode.PlatformProvided;
                return false;
        }
    }
}
