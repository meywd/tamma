using Tamma.Core.Enums;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — who we're resolving entitlements for. SaaS →
/// <see cref="ForTenant"/>; single-user → <see cref="ForUser"/> (the sole
/// user, resolved to their personal tenant). Mirrors the
/// <c>(userId | tenantId)</c> XOR principal of the prompt store: exactly one
/// of the two ids is set. A <see cref="EntitlementPrincipal"/> is a value.
/// </summary>
public readonly record struct EntitlementPrincipal
{
    /// <summary>SaaS principal — the tenant that owns the entitlements.</summary>
    public Guid? TenantId { get; private init; }

    /// <summary>single-user principal — the sole user (→ personal tenant).</summary>
    public Guid? UserId { get; private init; }

    /// <summary>SaaS: resolve directly by tenant id (from <c>ITenantContext</c>).</summary>
    public static EntitlementPrincipal ForTenant(Guid tenantId) => new() { TenantId = tenantId };

    /// <summary>single-user: resolve the sole user's personal tenant.</summary>
    public static EntitlementPrincipal ForUser(Guid userId) => new() { UserId = userId };
}

/// <summary>
/// Story 34-6 — one resolved quota line. <see cref="LimitValue"/> <c>null</c> =
/// unlimited. <see cref="Period"/> is <c>monthly</c> | <c>total</c>;
/// <see cref="OverageMode"/> is <c>block</c> | <c>allow</c> | <c>meter</c>
/// (mirrors <c>PlanEntitlement</c>'s domains verbatim).
/// </summary>
public sealed record ResolvedEntitlement(
    EntitlementMetricKey MetricKey,
    long? LimitValue,
    string Period,
    string OverageMode);

/// <summary>
/// Story 34-6 — the complete, closed entitlement map: EVERY
/// <see cref="EntitlementMetricKey"/> member is present (a missing catalog row
/// backfills the documented default, so consumers can index any metric without
/// a null-check). Carries the pinned plan coordinates
/// (<see cref="PlanId"/>/<see cref="PlanVersion"/>/<see cref="IsCustom"/>) so
/// callers can audit the source. Immutable value.
/// </summary>
public sealed record ResolvedEntitlements(
    Guid TenantId,
    Guid PlanId,
    int PlanVersion,
    bool IsCustom,
    IReadOnlyDictionary<EntitlementMetricKey, ResolvedEntitlement> Limits)
{
    /// <summary>
    /// Indexer over the closed map. Never throws for a valid enum member — the
    /// map is complete by construction (see
    /// <see cref="EntitlementDefaults.BuildClosedMap"/>).
    /// </summary>
    public ResolvedEntitlement Get(EntitlementMetricKey key) => Limits[key];
}

/// <summary>
/// Story 34-6 — non-enforcing headroom calc. The single shared over/remaining
/// computation the sibling Enforcement epic and both dashboards consume so the
/// math can never diverge. <see cref="Remaining"/> <c>null</c> = unlimited;
/// <see cref="CurrentUsage"/> <c>null</c> = usage unavailable (metering-only
/// metric until Epic 35 supplies its reader).
/// </summary>
public sealed record EntitlementHeadroom(
    EntitlementMetricKey MetricKey,
    long? LimitValue,
    long? CurrentUsage,
    long? Remaining,
    bool IsOver,
    double? OveragePercent);

/// <summary>
/// Story 34-6 — code-owned defaults + the closed-map builder. Centralised so
/// the documented "missing entitlement row ⇒ most-restrictive default" rule
/// lives in exactly one place (AC2). A missing metric row NEVER produces an
/// empty/absent entry — it produces a <c>limit 0 / monthly / block</c> line.
/// </summary>
public static class EntitlementDefaults
{
    /// <summary>Documented per-metric default reset window when the plan omits a row.</summary>
    public const string DefaultPeriod = "monthly";

    /// <summary>Documented per-metric default overage behaviour when the plan omits a row.</summary>
    public const string DefaultOverageMode = "block";

    /// <summary>Documented per-metric default limit when the plan omits a row (most restrictive).</summary>
    public const long DefaultLimit = 0;

    /// <summary>The closed set of every metric key — the map is always exactly this set.</summary>
    public static IReadOnlyList<EntitlementMetricKey> AllMetrics { get; } =
        Enum.GetValues<EntitlementMetricKey>();

    /// <summary>The documented default line for a metric with no catalog row.</summary>
    public static ResolvedEntitlement DefaultFor(EntitlementMetricKey metric) =>
        new(metric, DefaultLimit, DefaultPeriod, DefaultOverageMode);

    /// <summary>
    /// Build the complete, closed map from a plan version's entitlement rows.
    /// Present rows win verbatim (including <c>LimitValue == null</c> ⇒
    /// unlimited); absent metrics backfill <see cref="DefaultFor"/>. Guarantees
    /// AC2: exactly the closed enum, never a subset.
    /// </summary>
    /// <param name="rows">The pinned plan version's entitlement projections.</param>
    /// <param name="onBackfill">
    /// Optional callback invoked once per backfilled (missing) metric — used by
    /// the resolver to WARN-log the default backfill.
    /// </param>
    public static IReadOnlyDictionary<EntitlementMetricKey, ResolvedEntitlement> BuildClosedMap(
        IEnumerable<PlanEntitlementView> rows,
        Action<EntitlementMetricKey>? onBackfill = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var byMetric = new Dictionary<EntitlementMetricKey, PlanEntitlementView>();
        foreach (var row in rows)
        {
            // Last write wins if the catalog ever produced a duplicate — the
            // catalog's own unique index prevents this, but be defensive.
            byMetric[row.MetricKey] = row;
        }

        var map = new Dictionary<EntitlementMetricKey, ResolvedEntitlement>(AllMetrics.Count);
        foreach (var metric in AllMetrics)
        {
            if (byMetric.TryGetValue(metric, out var row))
            {
                map[metric] = new ResolvedEntitlement(
                    metric, row.LimitValue, row.Period, row.OverageMode);
            }
            else
            {
                onBackfill?.Invoke(metric);
                map[metric] = DefaultFor(metric);
            }
        }

        return map;
    }

    /// <summary>
    /// Pure, non-enforcing headroom calc shared by the resolver, the endpoints,
    /// the sibling Enforcement epic, and both dashboards. Unlimited
    /// (<paramref name="limitValue"/> <c>null</c>) short-circuits to
    /// <c>Remaining = null, IsOver = false</c> regardless of usage. A zero limit
    /// yields <c>OveragePercent = null</c> (division guard) but still flags
    /// <c>IsOver</c> when usage &gt; 0.
    /// </summary>
    public static EntitlementHeadroom ComputeHeadroom(
        EntitlementMetricKey metric, long? limitValue, long? currentUsage)
    {
        // Unlimited: no ceiling, never over, remaining is meaningless (null).
        if (limitValue is null)
        {
            return new EntitlementHeadroom(
                metric, LimitValue: null, CurrentUsage: currentUsage,
                Remaining: null, IsOver: false, OveragePercent: null);
        }

        var limit = limitValue.Value;

        // Usage unavailable (metering-only metric, Epic 35): report the limit
        // but no over/remaining math — CurrentUsage/Remaining/OveragePercent null.
        if (currentUsage is null)
        {
            return new EntitlementHeadroom(
                metric, LimitValue: limit, CurrentUsage: null,
                Remaining: null, IsOver: false, OveragePercent: null);
        }

        var usage = currentUsage.Value;
        var remaining = Math.Max(0, limit - usage);
        var isOver = usage > limit;
        double? overagePercent = limit > 0 ? (double)usage / limit * 100 : null;

        return new EntitlementHeadroom(
            metric, LimitValue: limit, CurrentUsage: usage,
            Remaining: remaining, IsOver: isOver, OveragePercent: overagePercent);
    }
}
