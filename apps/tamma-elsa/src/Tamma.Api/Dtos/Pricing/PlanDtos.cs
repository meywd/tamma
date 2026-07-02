namespace Tamma.Api.Dtos.Pricing;

/// <summary>
/// Story 34-2 — request/response DTOs for the plan-catalog admin API and the
/// public read routes. These are the wire shapes; the endpoint layer maps them
/// onto the typed <c>PlanDraftSpec</c> the immutable <c>PlanVersionEditor</c>
/// consumes (Story 34-1 owns the versioning invariants — 34-2 never mutates a
/// row in place). Responses are the typed <c>PlanSnapshot</c> projection from
/// Story 34-1 (AC13 — no raw EF entity is ever serialised).
/// </summary>
public sealed record CreatePlanRequest(
    string Slug,
    string DisplayName,
    string BillingInterval,
    IReadOnlyList<PlanFeatureDto>? Features,
    IReadOnlyList<PlanEntitlementDto>? Entitlements,
    IReadOnlyList<PlanPriceDto>? Prices);

/// <summary>
/// Body for <c>PUT /api/admin/pricing/plans/{slug}</c> — versions the existing
/// active plan. Header fields are optional overrides (null ⇒ copy from the prior
/// version); child collections are full replacements when non-null (null ⇒ copy
/// the prior version's children), matching <c>PlanDraftSpec</c> semantics.
/// </summary>
public sealed record VersionPlanRequest(
    string? DisplayName = null,
    string? BillingInterval = null,
    IReadOnlyList<PlanFeatureDto>? Features = null,
    IReadOnlyList<PlanEntitlementDto>? Entitlements = null,
    IReadOnlyList<PlanPriceDto>? Prices = null);

/// <summary>
/// Body for <c>POST /api/admin/pricing/plans/custom</c> — mints a bespoke
/// enterprise plan bound to exactly one tenant. The slug is server-derived
/// (<c>custom-{tenantId:N}-{n}</c>) so the binding is recoverable without a
/// dedicated column; <see cref="MakePublic"/> exists only as the fail-loud guard
/// (AC5): a custom plan must NEVER surface in the public catalog, so a request
/// that asks for public visibility is rejected 400.
/// </summary>
public sealed record CreateCustomPlanRequest(
    Guid TenantId,
    string DisplayName,
    string BillingInterval,
    IReadOnlyList<PlanFeatureDto>? Features,
    IReadOnlyList<PlanEntitlementDto>? Entitlements,
    IReadOnlyList<PlanPriceDto>? Prices,
    bool? MakePublic = null);

/// <summary>A typed feature flag on the wire (bool capability OR string value).</summary>
public sealed record PlanFeatureDto(string FeatureKey, bool? BoolValue, string? StringValue);

/// <summary>
/// A typed quota entitlement on the wire. <c>MetricKey</c> is the snake_case
/// string validated against the <c>EntitlementMetricKey</c> enum (AC8);
/// <c>LimitValue == null</c> ⇒ unlimited.
/// </summary>
public sealed record PlanEntitlementDto(string MetricKey, long? LimitValue, string Period, string OverageMode);

/// <summary>
/// A pricing row on the wire. <c>PricingMode</c> is validated against
/// <c>platform_provided | byok</c> (AC8); <c>MeteredComponentJson</c> is stored
/// verbatim (defaults to <c>{}</c>).
/// </summary>
public sealed record PlanPriceDto(string PricingMode, decimal RecurringUsd, decimal SeatUsd, string? MeteredComponentJson);
