using Tamma.Data.Entities;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-4 — the read DTO returned by <c>GET /billing/subscription</c> and by
/// every lifecycle mutation. A tenant with no mirror row resolves to
/// <see cref="FreeDefault"/> (free tier, active, one seat).
/// </summary>
public sealed record SubscriptionProjection(
    string PlanSlug,
    string Status,
    DateTime? CurrentPeriodStart,
    DateTime? CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    DateTime? TrialEnd,
    int Seats,
    string? ScheduledPlanSlug,
    DateTime? ScheduledEffectiveAt)
{
    /// <summary>The free-tier default projection for a tenant with no subscription (AC9).</summary>
    public static SubscriptionProjection FreeDefault() =>
        new("free", "active", null, null, false, null, 1, null, null);

    /// <summary>Project a persisted mirror row onto the wire shape.</summary>
    public static SubscriptionProjection From(BillingSubscription s) =>
        new(
            s.PlanSlug,
            s.Status,
            s.CurrentPeriodStart == default ? null : s.CurrentPeriodStart,
            s.CurrentPeriodEnd == default ? null : s.CurrentPeriodEnd,
            s.CancelAtPeriodEnd,
            s.TrialEnd,
            s.Seats,
            s.ScheduledPlanSlug,
            s.ScheduledEffectiveAt);
}
