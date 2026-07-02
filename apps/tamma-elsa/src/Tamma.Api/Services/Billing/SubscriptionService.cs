using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-4 — the subscription lifecycle orchestrator. Calls Stripe via the
/// 35-1 client factory, then funnels every Stripe result through the shared
/// <see cref="SubscriptionMirrorUpdater"/> so the mirror + <c>Tenant.Plan</c>
/// lockstep + DCB event live in one place (also shared with the 35-5 webhook).
///
/// <para>Idempotency: every mutating Stripe call carries a deterministic
/// <see cref="RequestOptions.IdempotencyKey"/> so a retry never double-applies
/// proration or mints a duplicate schedule (AC10). Single-user short-circuits on
/// <see cref="IBillingProvider.IsEnabled"/> — zero Stripe calls (AC11).</para>
/// </summary>
public sealed class SubscriptionService : ISubscriptionService
{
    /// <summary>Stable code for the seat-floor conflict (AC6) — surfaced as 409.</summary>
    public const string SeatsBelowActiveMembersCode = "seats_below_active_members";

    public const string SaasOnlyCode = "BILLING.SUBSCRIPTION.SAAS_ONLY";
    public const string NoActiveSubscriptionCode = "BILLING.SUBSCRIPTION.NO_ACTIVE_SUBSCRIPTION";
    public const string NoCustomerCode = "BILLING.SUBSCRIPTION.NO_CUSTOMER";

    private readonly IBillingProvider _provider;
    private readonly IStripeServicesFactory _stripeFactory;
    private readonly IBillingCatalog _catalog;
    private readonly IBillingSubscriptionRepository _repo;
    private readonly SubscriptionMirrorUpdater _mirror;
    private readonly ControlPlaneDbContext _db;
    private readonly ITenantMembershipRepository _memberships;
    private readonly BillingOptions _options;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(
        IBillingProvider provider,
        IStripeServicesFactory stripeFactory,
        IBillingCatalog catalog,
        IBillingSubscriptionRepository repo,
        SubscriptionMirrorUpdater mirror,
        ControlPlaneDbContext db,
        ITenantMembershipRepository memberships,
        IOptions<BillingOptions> options,
        ILogger<SubscriptionService> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(stripeFactory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _provider = provider;
        _stripeFactory = stripeFactory;
        _catalog = catalog;
        _repo = repo;
        _mirror = mirror;
        _db = db;
        _memberships = memberships;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CheckoutResult> CreateCheckoutSessionAsync(
        Guid tenantId, string planSlug, int? seats, int? trialDays, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planSlug);
        EnsureSaas();

        var customer = await ResolveCustomerAsync(tenantId, ct).ConfigureAwait(false);
        var catalog = await _catalog.GetBySlugAsync(planSlug, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(catalog.StripePriceId))
        {
            throw new TammaError(
                "BILLING.CATALOG.NO_PRICE",
                $"Plan '{planSlug}' has no Stripe base price id in the catalog — run `seed-billing`.",
                new Dictionary<string, object?> { ["planSlug"] = planSlug },
                retryable: false, severity: TammaErrorSeverity.High);
        }

        var lineItems = new List<Stripe.Checkout.SessionLineItemOptions>
        {
            new() { Price = catalog.StripePriceId, Quantity = 1 },
        };
        if (seats is > 1 && !string.IsNullOrEmpty(catalog.SeatsPriceId))
        {
            lineItems.Add(new Stripe.Checkout.SessionLineItemOptions
            {
                Price = catalog.SeatsPriceId,
                Quantity = seats.Value,
            });
        }

        var sessionOptions = new Stripe.Checkout.SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customer.StripeCustomerId,
            LineItems = lineItems,
            SuccessUrl = _options.CheckoutSuccessUrl,
            CancelUrl = _options.CheckoutCancelUrl,
        };
        if (trialDays is > 0)
        {
            sessionOptions.SubscriptionData = new Stripe.Checkout.SessionSubscriptionDataOptions
            {
                TrialPeriodDays = trialDays.Value,
            };
        }

        var stripe = await _stripeFactory.CreateAsync(ct).ConfigureAwait(false);
        var idempotencyKey = $"sub-checkout-{tenantId:D}-{planSlug}";
        _logger.LogDebug(
            "Creating Checkout session for tenant {TenantId} (planSlug={PlanSlug}, seats={Seats}, "
            + "trialDays={TrialDays}, idempotencyKey set).",
            tenantId, planSlug, seats, trialDays);

        var session = await stripe.CheckoutSessions
            .CreateAsync(sessionOptions, Idem(idempotencyKey), ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Checkout session created for tenant {TenantId} (planSlug={PlanSlug}, sessionId={SessionId}).",
            tenantId, planSlug, session.Id);

        return new CheckoutResult(session.Url, session.Id);
    }

    /// <inheritdoc />
    public async Task<SubscriptionProjection> ChangePlanAsync(
        Guid tenantId, string newPlanSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPlanSlug);
        EnsureSaas();

        var mirror = await ResolveActiveMirrorAsync(tenantId, ct).ConfigureAwait(false);
        var currentPlan = await GetActivePlanAsync(mirror.PlanSlug, ct).ConfigureAwait(false);
        var targetPlan = await GetActivePlanAsync(newPlanSlug, ct).ConfigureAwait(false);
        var targetCatalog = await _catalog.GetBySlugAsync(newPlanSlug, ct).ConfigureAwait(false);

        var stripe = await _stripeFactory.CreateAsync(ct).ConfigureAwait(false);
        var isUpgrade = targetPlan.MonthlyPriceUsd >= currentPlan.MonthlyPriceUsd;
        _logger.LogDebug(
            "Plan change for tenant {TenantId}: {CurrentSlug} (${CurrentPrice}) → {TargetSlug} "
            + "(${TargetPrice}) — {Decision}.",
            tenantId, mirror.PlanSlug, currentPlan.MonthlyPriceUsd, newPlanSlug,
            targetPlan.MonthlyPriceUsd, isUpgrade ? "upgrade/prorate" : "downgrade/schedule");

        if (isUpgrade)
        {
            // Immediate upgrade with proration — swap the base item's price.
            var sub = await stripe.Subscriptions
                .GetAsync(mirror.StripeSubscriptionId, null, null, ct).ConfigureAwait(false);
            var baseItemId = ResolveBaseItemId(sub);

            var updateOptions = new SubscriptionUpdateOptions
            {
                ProrationBehavior = "create_prorations",
                Items = new List<SubscriptionItemOptions>
                {
                    new() { Id = baseItemId, Price = targetCatalog.StripePriceId },
                },
            };
            var idempotencyKey =
                $"sub-change-{tenantId:D}-{newPlanSlug}-{mirror.CurrentPeriodEnd:yyyyMMdd}";
            var updated = await stripe.Subscriptions
                .UpdateAsync(mirror.StripeSubscriptionId, updateOptions, Idem(idempotencyKey), ct)
                .ConfigureAwait(false);

            return await _mirror.ApplyAsync(
                tenantId, updated, SubscriptionMirrorUpdater.TransitionUpgraded, ct)
                .ConfigureAwait(false);
        }

        // Downgrade — schedule at period end via a Stripe Subscription Schedule.
        // The live PlanSlug/Tenant.Plan stay at the current (higher) plan until the
        // rollover webhook fires (AC3); the mirror records the scheduled target.
        var scheduleOptions = new SubscriptionScheduleCreateOptions
        {
            FromSubscription = mirror.StripeSubscriptionId,
        };
        var scheduleKey =
            $"sub-downgrade-{tenantId:D}-{newPlanSlug}-{mirror.CurrentPeriodEnd:yyyyMMdd}";
        var schedule = await stripe.SubscriptionSchedules
            .CreateAsync(scheduleOptions, Idem(scheduleKey), ct)
            .ConfigureAwait(false);

        return await _mirror.RecordScheduledDowngradeAsync(
            mirror, newPlanSlug, mirror.CurrentPeriodEnd, schedule.Id, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SubscriptionProjection> CancelAsync(
        Guid tenantId, bool atPeriodEnd, CancellationToken ct = default)
    {
        EnsureSaas();
        var mirror = await ResolveActiveMirrorAsync(tenantId, ct).ConfigureAwait(false);
        var stripe = await _stripeFactory.CreateAsync(ct).ConfigureAwait(false);
        var idempotencyKey =
            $"sub-cancel-{tenantId:D}-{atPeriodEnd}-{mirror.CurrentPeriodEnd:yyyyMMdd}";

        if (atPeriodEnd)
        {
            var updated = await stripe.Subscriptions
                .UpdateAsync(
                    mirror.StripeSubscriptionId,
                    new SubscriptionUpdateOptions { CancelAtPeriodEnd = true },
                    Idem(idempotencyKey), ct)
                .ConfigureAwait(false);
            return await _mirror.ApplyAsync(
                tenantId, updated, SubscriptionMirrorUpdater.TransitionCanceledAtPeriodEnd, ct)
                .ConfigureAwait(false);
        }

        var canceled = await stripe.Subscriptions
            .CancelAsync(mirror.StripeSubscriptionId, null, Idem(idempotencyKey), ct)
            .ConfigureAwait(false);
        return await _mirror.ApplyAsync(
            tenantId, canceled, SubscriptionMirrorUpdater.TransitionCanceled, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SubscriptionProjection> ChangeSeatsAsync(
        Guid tenantId, int seats, CancellationToken ct = default)
    {
        EnsureSaas();
        if (seats < 1)
        {
            throw new TammaError(
                "BILLING.SUBSCRIPTION.INVALID_SEATS",
                "Seat count must be at least 1.",
                new Dictionary<string, object?> { ["seats"] = seats },
                retryable: false, severity: TammaErrorSeverity.Low);
        }

        var mirror = await ResolveActiveMirrorAsync(tenantId, ct).ConfigureAwait(false);

        // Seat floor is enforced BEFORE any Stripe call (AC6) — a rejected decrease
        // never mutates Stripe.
        var activeMembers = (await _memberships.ListAllByTenantAsync(tenantId).ConfigureAwait(false)).Count;
        if (seats < activeMembers)
        {
            _logger.LogWarning(
                "Seat decrease rejected for tenant {TenantId}: requested {Requested} < active members "
                + "{ActiveMembers}.", tenantId, seats, activeMembers);
            throw new TammaError(
                SeatsBelowActiveMembersCode,
                $"Cannot set seats to {seats}: the tenant has {activeMembers} active members. "
                + "Remove members first.",
                new Dictionary<string, object?>
                {
                    ["requested"] = seats,
                    ["activeMembers"] = activeMembers,
                },
                retryable: false, severity: TammaErrorSeverity.Medium);
        }

        var catalog = await _catalog.GetBySlugAsync(mirror.PlanSlug, ct).ConfigureAwait(false);
        var stripe = await _stripeFactory.CreateAsync(ct).ConfigureAwait(false);
        var sub = await stripe.Subscriptions
            .GetAsync(mirror.StripeSubscriptionId, null, null, ct).ConfigureAwait(false);

        // Update the existing seats item's quantity, or add one if the sub has none.
        var seatItemId = ResolveItemIdByPrice(sub, catalog.SeatsPriceId);
        var itemOptions = seatItemId is null
            ? new SubscriptionItemOptions { Price = catalog.SeatsPriceId, Quantity = seats }
            : new SubscriptionItemOptions { Id = seatItemId, Quantity = seats };

        var idempotencyKey = $"sub-seats-{tenantId:D}-{seats}-{mirror.CurrentPeriodEnd:yyyyMMdd}";
        var updated = await stripe.Subscriptions
            .UpdateAsync(
                mirror.StripeSubscriptionId,
                new SubscriptionUpdateOptions
                {
                    ProrationBehavior = "create_prorations",
                    Items = new List<SubscriptionItemOptions> { itemOptions },
                },
                Idem(idempotencyKey), ct)
            .ConfigureAwait(false);

        return await _mirror.ApplyAsync(
            tenantId, updated, SubscriptionMirrorUpdater.TransitionSeatsChanged, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SubscriptionProjection> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var mirror = await _repo.GetActiveByTenantAsync(tenantId, ct).ConfigureAwait(false);
        return mirror is null ? SubscriptionProjection.FreeDefault() : SubscriptionProjection.From(mirror);
    }

    // ── helpers ──

    private void EnsureSaas()
    {
        if (_provider.IsEnabled) return;
        throw new TammaError(
            SaasOnlyCode,
            "Billing is a SaaS-only feature; subscription management is unavailable in single-user mode.",
            new Dictionary<string, object?>(),
            retryable: false, severity: TammaErrorSeverity.Low);
    }

    private async Task<BillingCustomer> ResolveCustomerAsync(Guid tenantId, CancellationToken ct)
    {
        var customer = await _db.BillingCustomers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (customer is null || string.IsNullOrEmpty(customer.StripeCustomerId))
        {
            throw new TammaError(
                NoCustomerCode,
                $"Tenant {tenantId:D} has no Stripe customer mapping yet — billing is not provisioned.",
                new Dictionary<string, object?> { ["tenantId"] = tenantId.ToString("D") },
                retryable: false, severity: TammaErrorSeverity.Medium);
        }
        return customer;
    }

    private async Task<BillingSubscription> ResolveActiveMirrorAsync(Guid tenantId, CancellationToken ct)
    {
        var mirror = await _repo.GetActiveByTenantAsync(tenantId, ct).ConfigureAwait(false);
        if (mirror is null || string.IsNullOrEmpty(mirror.StripeSubscriptionId))
        {
            throw new TammaError(
                NoActiveSubscriptionCode,
                $"Tenant {tenantId:D} has no active Stripe subscription to modify. "
                + "Start one via checkout first.",
                new Dictionary<string, object?> { ["tenantId"] = tenantId.ToString("D") },
                retryable: false, severity: TammaErrorSeverity.Medium);
        }
        return mirror;
    }

    private async Task<Tamma.Data.Entities.Plan> GetActivePlanAsync(string slug, CancellationToken ct)
    {
        var plan = await _db.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Slug == slug && p.Status == "active", ct)
            .ConfigureAwait(false);
        if (plan is null)
        {
            throw new TammaError(
                "BILLING.SUBSCRIPTION.UNKNOWN_PLAN",
                $"No active plan for slug '{slug}'.",
                new Dictionary<string, object?> { ["slug"] = slug },
                retryable: false, severity: TammaErrorSeverity.Medium);
        }
        return plan;
    }

    /// <summary>The base subscription item id (first item, or the non-seats item).</summary>
    private static string ResolveBaseItemId(Stripe.Subscription sub)
    {
        var items = sub.Items?.Data;
        if (items is null || items.Count == 0)
        {
            throw new TammaError(
                "BILLING.SUBSCRIPTION.NO_ITEMS",
                "Stripe subscription has no items to update.",
                new Dictionary<string, object?> { ["subscriptionId"] = sub.Id },
                retryable: false, severity: TammaErrorSeverity.High);
        }
        return items[0].Id;
    }

    private static string? ResolveItemIdByPrice(Stripe.Subscription sub, string? priceId)
    {
        if (string.IsNullOrEmpty(priceId)) return null;
        return sub.Items?.Data?
            .FirstOrDefault(i => string.Equals(i.Price?.Id, priceId, StringComparison.Ordinal))?.Id;
    }

    private static RequestOptions Idem(string key) => new() { IdempotencyKey = key };
}
