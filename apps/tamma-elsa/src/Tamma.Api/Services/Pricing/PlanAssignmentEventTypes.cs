namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-4 — DCB event type names emitted by <see cref="PlanAssignmentService"/>
/// to the control-plane <c>platform_events</c> store. Pattern
/// <c>AGGREGATE.ACTION.STATUS</c>. Both live under the <c>TENANT.PLAN.</c>
/// prefix that Story 34-6's <c>EntitlementCacheInvalidationListener</c>
/// subscribes for per-tenant snapshot eviction, so every assignment change
/// evicts exactly that tenant's cached entitlements.
///
/// <para>The legacy <c>PLAN.UPDATED</c> tag (the ad-hoc emit the pre-34-4
/// <c>UpdateTenantPlan</c> path used) is superseded by
/// <see cref="TenantPlanChanged"/>; a <c>supersedesLegacy = PLAN.UPDATED</c> tag
/// is carried for one release so the admin dashboard timeline does not blank
/// out mid-migration.</para>
/// </summary>
public static class PlanAssignmentEventTypes
{
    /// <summary>Emitted on assign + scheduled-activation (upgrade/downgrade/lateral).</summary>
    public const string TenantPlanChanged = "TENANT.PLAN.CHANGED";

    /// <summary>Emitted when a cancellation is scheduled (drop → plan_free at boundary).</summary>
    public const string TenantPlanCancelled = "TENANT.PLAN.CANCELLED";

    /// <summary>Superseded legacy tag kept for one-release dashboard back-compat.</summary>
    public const string LegacyPlanUpdated = "PLAN.UPDATED";
}
