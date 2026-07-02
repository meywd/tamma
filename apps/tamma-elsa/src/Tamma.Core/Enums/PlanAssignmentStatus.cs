namespace Tamma.Core.Enums;

/// <summary>
/// Story 34-4 — lifecycle status of a <c>TenantPlanAssignment</c> row. Persisted
/// as the lower-case string (never a numeric ordinal — the DB CHECK constraint
/// pins the exact strings), matching the string-backed style of the 34-1 plan
/// catalog (<c>Plan.Status</c>).
///
/// <list type="bullet">
///   <item><description><c>active</c> — the tenant's CURRENT plan version. A
///     partial unique index guarantees at most one active row per tenant.</description></item>
///   <item><description><c>scheduled</c> — a future assignment that a boundary
///     activation task promotes to <c>active</c> once its <c>EffectiveFrom</c>
///     is reached (e.g. the <c>plan_free</c> row a cancellation queues at the
///     period boundary).</description></item>
///   <item><description><c>cancelled</c> — a superseded assignment. Its
///     <c>EffectiveTo</c> is stamped at the instant it stopped being active
///     (the proration boundary Billing reads).</description></item>
/// </list>
/// </summary>
public static class PlanAssignmentStatus
{
    public const string Active = "active";
    public const string Scheduled = "scheduled";
    public const string Cancelled = "cancelled";

    /// <summary>The closed set — used by validation and the model CHECK constraint.</summary>
    public static readonly System.Collections.Generic.IReadOnlyList<string> All =
        new[] { Active, Scheduled, Cancelled };
}
