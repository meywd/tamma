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
}
