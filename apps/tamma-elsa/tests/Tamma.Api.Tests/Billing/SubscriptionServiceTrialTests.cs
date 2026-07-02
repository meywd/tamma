using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Billing;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-4 (AC5) — trial handling: a checkout with <c>trialDays</c> passes the
/// trial period to Stripe; a trialing subscription applied through the shared
/// updater sets <c>Status=trialing</c> + <c>TrialEnd</c>; conversion/expiry emit
/// <c>BILLING.SUBSCRIPTION.TRIAL_ENDED</c> (covered in the updater tests).
/// </summary>
[TestFixture]
public class SubscriptionServiceTrialTests
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Trial = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Checkout_With_TrialDays_Sets_TrialPeriod()
    {
        var h = SubscriptionHarness.Create(nameof(Checkout_With_TrialDays_Sets_TrialPeriod));
        var tenantId = Guid.NewGuid();
        h.SeedCustomer(tenantId);
        h.SeedCatalog("team", "price_team");

        Stripe.Checkout.SessionCreateOptions? captured = null;
        h.Checkout.Setup(c => c.CreateAsync(
                It.IsAny<Stripe.Checkout.SessionCreateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stripe.Checkout.SessionCreateOptions, Stripe.RequestOptions, CancellationToken>(
                (o, _, _) => captured = o)
            .ReturnsAsync(new Stripe.Checkout.Session { Id = "cs_trial", Url = "https://checkout/trial" });

        await h.Service.CreateCheckoutSessionAsync(tenantId, "team", seats: null, trialDays: 30);

        captured!.SubscriptionData!.TrialPeriodDays.Should().Be(30);
    }

    [Test]
    public async Task Trialing_Subscription_Materializes_With_TrialEnd()
    {
        var h = SubscriptionHarness.Create(nameof(Trialing_Subscription_Materializes_With_TrialEnd));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "free");

        var sub = SubscriptionHarness.MakeSub("sub_1", "trialing", "price_team", Start, End, trialEnd: Trial);
        var projection = await h.Updater.ApplyAsync(
            tenantId, sub, SubscriptionMirrorUpdater.TransitionCreated);

        projection.Status.Should().Be("trialing");
        projection.TrialEnd.Should().Be(Trial);
        var mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.Status.Should().Be("trialing");
        mirror.TrialEnd.Should().Be(Trial);
    }
}
