namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — DCB event type names emitted by <see cref="EntitlementService"/>
/// to the control-plane <c>platform_events</c> store (CP-resident, same home as
/// the <see cref="PlanCatalogEventTypes"/> catalog events). Pattern:
/// <c>AGGREGATE.ACTION.STATUS</c>.
///
/// <para>Emitted on cache-miss resolve / admin read (SUCCESS) and on a
/// resolution failure (FAILED) — NOT on every cache hit (AC10).</para>
/// </summary>
public static class EntitlementEventTypes
{
    /// <summary>A tenant's entitlement set was resolved (cache-miss or admin read).</summary>
    public const string ResolvedSuccess = "ENTITLEMENT.RESOLVED.SUCCESS";

    /// <summary>Resolution failed (no active assignment / catalog unavailable).</summary>
    public const string ResolvedFailed = "ENTITLEMENT.RESOLVED.FAILED";

    // ── Consumed (not emitted here) — cache-invalidation triggers. ──

    /// <summary>
    /// Type-prefix the cache-invalidation listener subscribes for per-tenant
    /// eviction. Story 34-4 emits <c>TENANT.PLAN.CHANGED</c> under this prefix
    /// on (re)assignment; the listener evicts exactly that tenant's snapshot.
    /// </summary>
    public const string TenantPlanChangedPrefix = "TENANT.PLAN.";

    /// <summary>
    /// Type-prefix the cache-invalidation listener subscribes for a full flush.
    /// Catches the 34-1/34-2 catalog-edit events (<c>PLAN.VERSION.CREATED</c>,
    /// <c>PLAN.DEPRECATED</c>) — a catalog change flushes the whole cache
    /// (cheap, and pinned snapshots re-read correctly on the next miss).
    /// </summary>
    public const string PlanCatalogPrefix = "PLAN.";
}
