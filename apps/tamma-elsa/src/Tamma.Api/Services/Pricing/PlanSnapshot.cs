using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-1 — fully-resolved, immutable read DTO for a single
/// <c>Plan</c> version: header + typed features + entitlements + prices. Returned
/// by <see cref="IPlanCatalogService"/>. A snapshot is a value (record) — once
/// assembled it never changes, which mirrors the immutability of the underlying
/// versioned rows (a deprecated version's snapshot is frozen forever).
/// </summary>
public sealed record PlanSnapshot(
    Guid PlanId,
    string Slug,
    string DisplayName,
    int Version,
    string Status,
    bool IsCustom,
    string BillingInterval,
    Guid? SupersedesPlanId,
    IReadOnlyList<PlanFeatureView> Features,
    IReadOnlyList<PlanEntitlementView> Entitlements,
    IReadOnlyList<PlanPriceView> Prices);

/// <summary>A typed feature flag projection.</summary>
public sealed record PlanFeatureView(string FeatureKey, bool? BoolValue, string? StringValue);

/// <summary>A typed quota entitlement projection (<c>LimitValue == null</c> ⇒ unlimited).</summary>
public sealed record PlanEntitlementView(
    EntitlementMetricKey MetricKey,
    long? LimitValue,
    string Period,
    string OverageMode);

/// <summary>A pricing-row projection. <c>MeteredComponent</c> is the raw jsonb string (stored verbatim).</summary>
public sealed record PlanPriceView(
    string PricingMode,
    decimal RecurringUsd,
    decimal SeatUsd,
    string MeteredComponent);
