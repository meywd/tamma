using Tamma.Core.Enums;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-4 — options for <see cref="IPlanAssignmentService.AssignAsync"/>.
/// </summary>
/// <param name="ActorUserId">The actor making the change; <c>null</c> = system/scheduler.</param>
/// <param name="Reason">Free-text audit note (admin note / self-service).</param>
/// <param name="Force">
/// Allow assigning a <c>deprecated</c> plan version (a <c>draft</c> is ALWAYS
/// rejected regardless). Default <c>false</c>.
/// </param>
/// <param name="Source">
/// Where the assignment originated — one of <c>admin</c> | <c>self-service</c> |
/// <c>scheduled-activation</c>. Stamped as the <c>source</c> event tag.
/// </param>
/// <param name="ActorEmail">Actor email for the audit breadcrumb (like <c>BuildAdminEvent</c>).</param>
/// <param name="ActorPlatformRole">Actor platform role for the audit breadcrumb.</param>
public sealed record AssignPlanOptions(
    Guid? ActorUserId = null,
    string? Reason = null,
    bool Force = false,
    string Source = "admin",
    string? ActorEmail = null,
    string? ActorPlatformRole = null);

/// <summary>
/// Story 34-4 — options for <see cref="IPlanAssignmentService.CancelAsync"/>.
/// </summary>
/// <param name="ActorUserId">The actor scheduling the cancel; <c>null</c> = system.</param>
/// <param name="Reason">Free-text audit note.</param>
/// <param name="Immediate">
/// When <c>true</c> the tenant drops to <c>plan_free</c> NOW instead of at the
/// current billing-interval boundary. Default <c>false</c> (period-end).
/// </param>
public sealed record CancelPlanOptions(
    Guid? ActorUserId = null,
    string? Reason = null,
    bool Immediate = false,
    string? ActorEmail = null,
    string? ActorPlatformRole = null);

/// <summary>
/// Story 34-4 — result of an assign / cancel / activate operation. Carries the
/// resulting assignment row, the classified change direction, any over-limit
/// downgrade <see cref="Warnings"/> (flagged, never blocking), and — for a
/// scheduled cancel — the boundary instant the drop takes effect.
/// </summary>
public sealed record PlanAssignmentResult(
    TenantPlanAssignment Assignment,
    PlanChangeDirection Direction,
    IReadOnlyList<EntitlementWarning> Warnings,
    DateTime? ScheduledEffectiveAt);

/// <summary>
/// Story 34-4 — a single over-limit downgrade warning: the tenant's current
/// usage of <paramref name="MetricKey"/> exceeds the NEW plan's limit. Purely
/// informational — this story flags, it never blocks (enforcement is a sibling
/// epic). <c>CurrentUsage</c> / <c>NewLimit</c> are <c>null</c> when unknown
/// (usage reader can't answer / unlimited).
/// </summary>
public sealed record EntitlementWarning(
    EntitlementMetricKey MetricKey,
    long? CurrentUsage,
    long? NewLimit);
