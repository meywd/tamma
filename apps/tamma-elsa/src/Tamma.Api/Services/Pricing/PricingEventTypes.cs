namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-5 — DCB event type names emitted by the admin margin-policy write
/// path to the control-plane <c>platform_events</c> store (mirrors
/// <c>PlanCatalogEventTypes</c> / <c>ProviderPricingEventTypes</c>). Pattern:
/// <c>AGGREGATE.ACTION.STATUS</c>. The pure engine emits NO events (it does no
/// I/O); only the versioning admin endpoint does.
/// </summary>
public static class PricingEventTypes
{
    /// <summary>A margin policy was versioned (supersede prior active + insert new active).</summary>
    public const string MarginUpdated = "PRICING.MARGIN.UPDATED";

    /// <summary>
    /// Story 34-3 (AC9) — a tenant enabled BYOK for a <c>(tenant, provider)</c>: their
    /// own key was stored in the Epic 29 cabinet and the authoritative
    /// <c>TenantProviderBilling</c> owner row flipped to <c>byok</c>. Tagged
    /// <c>tenantId</c>, <c>provider</c> (raw provider identity), <c>mode=byok</c>.
    /// </summary>
    public const string ByokEnabled = "PRICING.BYOK.ENABLED";

    /// <summary>
    /// Story 34-3 (AC9) — a tenant disabled BYOK for a <c>(tenant, provider)</c>: the
    /// owner row flipped back to <c>platform</c> and the cabinet secret was retired.
    /// Tagged <c>tenantId</c>, <c>provider</c> (raw provider identity), <c>mode=platform</c>.
    /// </summary>
    public const string ByokDisabled = "PRICING.BYOK.DISABLED";
}
