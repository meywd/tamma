using Microsoft.EntityFrameworkCore;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-4 — the SINGLE place a Stripe <see cref="Stripe.Subscription"/> is
/// turned into the local <see cref="BillingSubscription"/> mirror + the
/// <c>Tenant.Plan</c>/<c>Tenant.PlanId</c> lockstep + the
/// <c>BILLING.SUBSCRIPTION.*</c> DCB event. Shared by BOTH the synchronous API
/// path (<see cref="SubscriptionService"/>) and the asynchronous webhook path
/// (<see cref="SubscriptionMirrorWebhookHandler"/>, called by the Story 35-5
/// processor) so the mirror logic and the no-drift lockstep exist in exactly one
/// place — the webhook reconciles, it never re-implements.
///
/// <para><b>Stripe is the state source of truth (AC13).</b> Status, period, and
/// trial end are copied from the Stripe object, never inferred from the API
/// request, so a stale request can never overwrite a newer Stripe-confirmed
/// state. The effective plan slug is reverse-resolved from the subscription's
/// base price id via the <c>billing_plan_prices</c> catalog.</para>
/// </summary>
public sealed class SubscriptionMirrorUpdater
{
    // ── Transition markers (drive the DCB event type + the lockstep rules) ──
    public const string TransitionCreated = "created";
    public const string TransitionUpgraded = "upgraded";
    public const string TransitionUpdated = "updated";
    public const string TransitionSeatsChanged = "seats_changed";
    public const string TransitionCanceledAtPeriodEnd = "canceled_at_period_end";
    public const string TransitionCanceled = "canceled";

    /// <summary>
    /// The <c>customer.subscription.trial_will_end</c> transition — fires ~3 days
    /// BEFORE the trial ends (the subscription is still <c>trialing</c>), so it
    /// emits <c>BILLING.SUBSCRIPTION.TRIAL_ENDING</c> (not <c>..._ENDED</c>).
    /// </summary>
    public const string TransitionTrialWillEnd = "trial_will_end";

    /// <summary>
    /// Raised when the effective plan slug has no active <c>Plan</c> row to lock
    /// <c>Tenant.Plan</c>/<c>PlanId</c> onto (e.g. a cancel to free with no seeded
    /// <c>free</c> plan). Fails LOUD so the transition surfaces/retries rather than
    /// silently leaving the tenant entitled to the old higher plan.
    /// </summary>
    public const string LockstepPlanMissingCode = "BILLING.SUBSCRIPTION.LOCKSTEP_PLAN_MISSING";

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "trialing", "active", "past_due", "canceled",
        "incomplete", "incomplete_expired", "unpaid",
    };

    // Terminal / non-entitling statuses fall back to the free plan for the
    // Tenant.Plan lockstep (immediate cancel / trial-expiry recompute quota to
    // free now — AC4/AC5). past_due keeps the plan live through dunning; incomplete
    // keeps the resolved plan (the initial charge is still in flight).
    private static readonly HashSet<string> FreeFallbackStatuses = new(StringComparer.Ordinal)
    {
        "canceled", "incomplete_expired", "unpaid",
    };

    private readonly ControlPlaneDbContext _db;
    private readonly IBillingSubscriptionRepository _repo;
    private readonly IEventRepository _events;
    private readonly TimeProvider _clock;
    private readonly ILogger<SubscriptionMirrorUpdater> _logger;

    public SubscriptionMirrorUpdater(
        ControlPlaneDbContext db,
        IBillingSubscriptionRepository repo,
        IEventRepository events,
        TimeProvider clock,
        ILogger<SubscriptionMirrorUpdater> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _repo = repo;
        _events = events;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Apply a Stripe subscription object onto the tenant's mirror + the
    /// <c>Tenant.Plan</c>/<c>Tenant.PlanId</c> lockstep in one control-plane
    /// transaction, then emit the transition's <c>BILLING.SUBSCRIPTION.*</c> DCB
    /// event and return the projection. Status/period/trialEnd/seats come from
    /// <paramref name="stripeSub"/> (AC13).
    /// </summary>
    public async Task<SubscriptionProjection> ApplyAsync(
        Guid tenantId, Stripe.Subscription stripeSub, string transition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stripeSub);
        ArgumentException.ThrowIfNullOrWhiteSpace(transition);

        var now = _clock.GetUtcNow().UtcDateTime;

        // Resolve the mirror: prefer the Stripe-id match (webhook reconcile), else
        // the tenant's active row (first API materialization).
        BillingSubscription? mirror = null;
        if (!string.IsNullOrEmpty(stripeSub.Id))
        {
            mirror = await _repo.GetByStripeSubscriptionIdAsync(stripeSub.Id, ct).ConfigureAwait(false);
        }
        mirror ??= await _repo.GetActiveByTenantAsync(tenantId, ct).ConfigureAwait(false);

        var isNew = mirror is null;
        if (mirror is null)
        {
            mirror = new BillingSubscription
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CreatedAt = now,
                Seats = 1,
            };
            await _repo.AddAsync(mirror, ct).ConfigureAwait(false);
        }

        var (resolvedSlug, resolvedSeats) = await ResolveSlugAndSeatsAsync(stripeSub, ct)
            .ConfigureAwait(false);

        var status = MapStatus(stripeSub.Status);
        var (periodStart, periodEnd) = ExtractPeriod(stripeSub);

        // Snapshot the mirror BEFORE mutation so the DCB event is emitted only when
        // the applied Stripe state actually changes the mirror (AC — no double
        // count). An API-initiated change applies once (emits here), then the
        // resulting Stripe webhook re-applies the SAME state; without this guard the
        // second apply would emit an identical BILLING.SUBSCRIPTION.* event and
        // double-count in Epic 36/37. The mirror write itself stays idempotent.
        var before = new MirrorSnapshot(mirror);

        mirror.StripeSubscriptionId = stripeSub.Id ?? mirror.StripeSubscriptionId;
        mirror.Status = status;
        if (periodStart is not null) mirror.CurrentPeriodStart = periodStart.Value;
        if (periodEnd is not null) mirror.CurrentPeriodEnd = periodEnd.Value;
        mirror.CancelAtPeriodEnd = stripeSub.CancelAtPeriodEnd;
        mirror.TrialEnd = stripeSub.TrialEnd;
        if (resolvedSeats is not null) mirror.Seats = resolvedSeats.Value;

        // The mirror's PlanSlug tracks the current EFFECTIVE plan (never the
        // scheduled downgrade target — that lives in ScheduledPlanSlug). A live
        // plan uses the resolved slug; a terminal/unpaid status collapses to free.
        var effectiveSlug = FreeFallbackStatuses.Contains(status)
            ? "free"
            : resolvedSlug ?? mirror.PlanSlug ?? "free";
        mirror.PlanSlug = effectiveSlug;

        // Clear a pending downgrade once it has rolled over (effective == scheduled)
        // or the subscription reached a terminal state.
        if (mirror.ScheduledPlanSlug is not null
            && (string.Equals(mirror.ScheduledPlanSlug, effectiveSlug, StringComparison.Ordinal)
                || FreeFallbackStatuses.Contains(status)))
        {
            mirror.ScheduledPlanSlug = null;
            mirror.ScheduledEffectiveAt = null;
            mirror.StripeScheduleId = null;
        }

        mirror.UpdatedAt = now;

        await ApplyTenantPlanLockstepAsync(tenantId, effectiveSlug, now, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Emit the DCB event ONLY when the applied Stripe state changed the mirror
        // (a brand-new mirror always counts as a change). A pure replay (webhook
        // echoing an already-applied API change) is a no-op emit.
        var changed = isNew || before.DiffersFrom(mirror);
        if (changed)
        {
            await EmitAsync(EventTypeFor(transition), tenantId, effectiveSlug, status, null)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Subscription {Transition} for tenant {TenantId}: planSlug={PlanSlug}, status={Status}, "
            + "seats={Seats} (new={IsNew}, emitted={Emitted}).",
            transition, tenantId, effectiveSlug, status, mirror.Seats, isNew, changed);

        return SubscriptionProjection.From(mirror);
    }

    /// <summary>
    /// Record a scheduled downgrade on the mirror WITHOUT changing the live
    /// <see cref="BillingSubscription.PlanSlug"/> or <c>Tenant.Plan</c> (the
    /// higher plan's quota stays live until the rollover webhook — AC3). Emits
    /// <c>BILLING.SUBSCRIPTION.UPDATED</c> carrying the <c>scheduledPlanSlug</c> tag.
    /// </summary>
    public async Task<SubscriptionProjection> RecordScheduledDowngradeAsync(
        BillingSubscription mirror, string scheduledSlug, DateTime effectiveAt,
        string stripeScheduleId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduledSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(stripeScheduleId);

        mirror.ScheduledPlanSlug = scheduledSlug;
        mirror.ScheduledEffectiveAt = effectiveAt;
        mirror.StripeScheduleId = stripeScheduleId;
        mirror.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAsync(
            BillingEvents.SubscriptionUpdatedType, mirror.TenantId,
            mirror.PlanSlug, mirror.Status, scheduledSlug).ConfigureAwait(false);

        _logger.LogInformation(
            "Subscription downgrade scheduled for tenant {TenantId}: {CurrentSlug} → {ScheduledSlug} "
            + "at {EffectiveAt:o} (planSlug/quota unchanged until rollover).",
            mirror.TenantId, mirror.PlanSlug, scheduledSlug, effectiveAt);

        return SubscriptionProjection.From(mirror);
    }

    /// <summary>Map the transition marker to the DCB event type (AC8).</summary>
    private static string EventTypeFor(string transition) => transition switch
    {
        TransitionCreated => BillingEvents.SubscriptionCreatedType,
        TransitionCanceled or TransitionCanceledAtPeriodEnd => BillingEvents.SubscriptionCanceledType,
        TransitionTrialWillEnd => BillingEvents.SubscriptionTrialEndingType,
        _ => BillingEvents.SubscriptionUpdatedType, // upgraded / updated / seats_changed
    };

    private static string MapStatus(string? stripeStatus) =>
        stripeStatus is not null && AllowedStatuses.Contains(stripeStatus) ? stripeStatus : "active";

    private static (DateTime? Start, DateTime? End) ExtractPeriod(Stripe.Subscription sub)
    {
        // Stripe.net 51.x moved current_period_* off the Subscription object onto
        // each SubscriptionItem. Use the first item's window (the base price item).
        var item = sub.Items?.Data?.FirstOrDefault();
        if (item is null) return (null, null);
        return (item.CurrentPeriodStart, item.CurrentPeriodEnd);
    }

    /// <summary>
    /// Reverse-resolve the effective plan slug (from the base price id) and the
    /// seat count (from the seats price id) using the <c>billing_plan_prices</c>
    /// catalog. Returns <c>(null, null)</c> when no catalog row matches — the
    /// caller then preserves the existing mirror slug/seats.
    /// </summary>
    private async Task<(string? Slug, int? Seats)> ResolveSlugAndSeatsAsync(
        Stripe.Subscription sub, CancellationToken ct)
    {
        var items = sub.Items?.Data;
        if (items is null || items.Count == 0) return (null, null);

        var priceIds = items
            .Select(i => i.Price?.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
        if (priceIds.Count == 0) return (null, null);

        var catalog = await _db.BillingPlanPrices
            .AsNoTracking()
            .Where(p => (p.StripePriceId != null && priceIds.Contains(p.StripePriceId))
                || (p.SeatsPriceId != null && priceIds.Contains(p.SeatsPriceId)))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (catalog.Count == 0) return (null, null);

        // Effective slug = the catalog row whose BASE price id is on the sub.
        var basePriceIds = new HashSet<string>(items
            .Select(i => i.Price?.Id).Where(id => id is not null)!, StringComparer.Ordinal);
        var slugRow = catalog.FirstOrDefault(
            p => p.StripePriceId is not null && basePriceIds.Contains(p.StripePriceId));
        var slug = slugRow?.PlanSlug;

        // Seats = quantity of the item whose price id matches the slug's seats price.
        int? seats = null;
        if (slugRow?.SeatsPriceId is not null)
        {
            var seatItem = items.FirstOrDefault(
                i => string.Equals(i.Price?.Id, slugRow.SeatsPriceId, StringComparison.Ordinal));
            if (seatItem is not null) seats = (int)seatItem.Quantity;
        }

        return (slug, seats);
    }

    /// <summary>
    /// No-drift lockstep (AC7) — mirror <c>Tenant.Plan</c> (legacy string) and the
    /// shadow <c>Tenant.PlanId</c> FK to the effective slug, exactly as
    /// <c>AdminTenantsEndpoints.UpdateTenantPlan</c> does. No-op when the tenant is
    /// already on that slug. Logged (not thrown) if the plan row is missing so a
    /// catalog gap never blocks the mirror save.
    /// </summary>
    private async Task ApplyTenantPlanLockstepAsync(
        Guid tenantId, string effectiveSlug, DateTime now, CancellationToken ct)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct)
            .ConfigureAwait(false);
        if (tenant is null)
        {
            _logger.LogError(
                "Subscription lockstep skipped: tenant {TenantId} not found — mirror/Tenant.Plan "
                + "may drift until the next webhook reconcile.", tenantId);
            return;
        }

        if (string.Equals(tenant.Plan, effectiveSlug, StringComparison.Ordinal)) return;

        var plan = await _db.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Slug == effectiveSlug && p.Status == "active", ct)
            .ConfigureAwait(false);
        if (plan is null)
        {
            // Fail LOUD (no silent fallback): a missing target plan row on a
            // money-critical transition would otherwise leave the tenant entitled
            // to the OLD (higher) plan — 34-6 reads the shadow PlanId. A `free`
            // plan must always be seeded; a gap must abort the transition so it
            // surfaces/retries rather than drift silently.
            _logger.LogError(
                "Subscription lockstep failed: no active Plan row for slug '{Slug}' — "
                + "aborting the transition for tenant {TenantId} (would drift entitlement).",
                effectiveSlug, tenantId);
            throw new TammaError(
                LockstepPlanMissingCode,
                $"No active Plan row for effective slug '{effectiveSlug}' — cannot lock "
                + $"Tenant.Plan for tenant {tenantId:D}. Seed the plan catalog.",
                new Dictionary<string, object?>
                {
                    ["tenantId"] = tenantId.ToString("D"),
                    ["effectiveSlug"] = effectiveSlug,
                },
                retryable: false, severity: TammaErrorSeverity.Critical);
        }

        _db.Entry(tenant).Property("PlanId").CurrentValue = plan.Id;
        tenant.Plan = plan.Slug;
        tenant.UpdatedAt = now;
    }

    private async Task EmitAsync(
        string type, Guid tenantId, string planSlug, string status, string? scheduledPlanSlug)
    {
        try
        {
            await _events.AppendAsync(
                BillingEvents.Subscription(type, tenantId, planSlug, status, scheduledPlanSlug))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A DCB append failure must not undo the already-committed mirror; the
            // webhook reconciles state and an operator can replay the audit event.
            _logger.LogError(ex,
                "Failed to append {Type} for tenant {TenantId} (mirror already saved).",
                type, tenantId);
        }
    }

    /// <summary>
    /// Immutable snapshot of the mirror fields that decide whether a transition is
    /// a real state change (drives the emit-once guard — Finding 3). Compared
    /// field-for-field against the mutated mirror; equal ⇒ pure replay ⇒ no emit.
    /// </summary>
    private readonly record struct MirrorSnapshot(
        string? StripeSubscriptionId,
        string? PlanSlug,
        string Status,
        DateTime CurrentPeriodStart,
        DateTime CurrentPeriodEnd,
        bool CancelAtPeriodEnd,
        DateTime? TrialEnd,
        int Seats,
        string? ScheduledPlanSlug,
        DateTime? ScheduledEffectiveAt,
        string? StripeScheduleId)
    {
        public MirrorSnapshot(BillingSubscription m) : this(
            m.StripeSubscriptionId, m.PlanSlug, m.Status, m.CurrentPeriodStart,
            m.CurrentPeriodEnd, m.CancelAtPeriodEnd, m.TrialEnd, m.Seats,
            m.ScheduledPlanSlug, m.ScheduledEffectiveAt, m.StripeScheduleId)
        {
        }

        /// <summary>True when any tracked field differs from the (mutated) mirror.</summary>
        public bool DiffersFrom(BillingSubscription m) => this != new MirrorSnapshot(m);
    }
}
