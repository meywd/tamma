namespace Tamma.Api.Services.Providers;

/// <summary>
/// Story 34-11 — DCB event type names emitted by the admin provider-pricing
/// write paths to the control-plane <c>platform_events</c> store (mirrors
/// <c>PlanCatalogEventTypes</c>). Pattern: <c>AGGREGATE.ACTION.STATUS</c>.
/// </summary>
public static class ProviderPricingEventTypes
{
    /// <summary>A new immutable model-price version was activated (supersede + insert).</summary>
    public const string PriceVersioned = "PROVIDER.PRICE.VERSIONED";

    /// <summary>A provider cost identity was registered.</summary>
    public const string Registered = "PROVIDER.REGISTERED";

    /// <summary>A provider's Status / DisplayName / AuthModel changed.</summary>
    public const string StatusChanged = "PROVIDER.STATUS_CHANGED";
}
