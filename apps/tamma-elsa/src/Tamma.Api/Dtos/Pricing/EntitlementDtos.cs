namespace Tamma.Api.Dtos.Pricing;

/// <summary>
/// Story 34-6 — wire shape for <c>GET /api/pricing/entitlements</c> and
/// <c>GET /api/admin/tenants/{id}/entitlements</c>. Embeds per-metric headroom
/// inline (currentUsage / remaining / isOver) so dashboards get resolution +
/// live headroom in ONE call.
/// </summary>
public sealed record ResolvedEntitlementsDto(
    string TenantId,
    string PlanId,
    int PlanVersion,
    bool IsCustom,
    IReadOnlyList<ResolvedEntitlementDto> Limits);

/// <summary>
/// Story 34-6 — one metric line: the resolved limit + the live headroom.
/// <c>limitValue</c>/<c>remaining</c> null = unlimited; <c>currentUsage</c> null
/// = usage unavailable (metering-only metric until Epic 35).
/// </summary>
public sealed record ResolvedEntitlementDto(
    string MetricKey,
    long? LimitValue,
    string Period,
    string OverageMode,
    long? CurrentUsage,
    long? Remaining,
    bool IsOver,
    double? OveragePercent);
