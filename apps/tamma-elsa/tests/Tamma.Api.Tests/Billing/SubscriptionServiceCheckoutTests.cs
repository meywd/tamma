using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-4 (AC2, AC10) — <c>CreateCheckoutSessionAsync</c> builds a
/// <c>mode=subscription</c> Stripe Checkout Session with the right base price,
/// optional seats line + trial days, and a deterministic idempotency key. No
/// local mirror row is created (the 35-5 webhook materializes it).
/// </summary>
[TestFixture]
public class SubscriptionServiceCheckoutTests
{
    [Test]
    public async Task Checkout_Builds_Session_With_Price_Seats_Trial_And_IdempotencyKey()
    {
        var h = SubscriptionHarness.Create(nameof(Checkout_Builds_Session_With_Price_Seats_Trial_And_IdempotencyKey));
        var tenantId = Guid.NewGuid();
        h.SeedCustomer(tenantId, "cus_abc");
        h.SeedCatalog("team", "price_team", "price_seats");

        Stripe.Checkout.SessionCreateOptions? captured = null;
        Stripe.RequestOptions? capturedRo = null;
        h.Checkout.Setup(c => c.CreateAsync(
                It.IsAny<Stripe.Checkout.SessionCreateOptions>(),
                It.IsAny<Stripe.RequestOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<Stripe.Checkout.SessionCreateOptions, Stripe.RequestOptions, CancellationToken>(
                (o, ro, _) => { captured = o; capturedRo = ro; })
            .ReturnsAsync(new Stripe.Checkout.Session { Id = "cs_1", Url = "https://checkout.stripe/cs_1" });

        var result = await h.Service.CreateCheckoutSessionAsync(tenantId, "team", seats: 3, trialDays: 14);

        result.CheckoutUrl.Should().Be("https://checkout.stripe/cs_1");
        result.StripeSessionId.Should().Be("cs_1");

        captured.Should().NotBeNull();
        captured!.Mode.Should().Be("subscription");
        captured.Customer.Should().Be("cus_abc");
        captured.LineItems.Should().HaveCount(2);
        captured.LineItems[0].Price.Should().Be("price_team");
        captured.LineItems[0].Quantity.Should().Be(1);
        captured.LineItems[1].Price.Should().Be("price_seats");
        captured.LineItems[1].Quantity.Should().Be(3);
        captured.SubscriptionData!.TrialPeriodDays.Should().Be(14);
        capturedRo!.IdempotencyKey.Should().Be($"sub-checkout-{tenantId:D}-team-s3-t14");

        // No local mirror row is created here (AC2).
        (await h.Db.BillingSubscriptions.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task Checkout_Without_Seats_Or_Trial_Has_Single_LineItem()
    {
        var h = SubscriptionHarness.Create(nameof(Checkout_Without_Seats_Or_Trial_Has_Single_LineItem));
        var tenantId = Guid.NewGuid();
        h.SeedCustomer(tenantId);
        h.SeedCatalog("team", "price_team", "price_seats");

        Stripe.Checkout.SessionCreateOptions? captured = null;
        h.Checkout.Setup(c => c.CreateAsync(
                It.IsAny<Stripe.Checkout.SessionCreateOptions>(),
                It.IsAny<Stripe.RequestOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<Stripe.Checkout.SessionCreateOptions, Stripe.RequestOptions, CancellationToken>(
                (o, _, _) => captured = o)
            .ReturnsAsync(new Stripe.Checkout.Session { Id = "cs_2", Url = "https://checkout.stripe/cs_2" });

        await h.Service.CreateCheckoutSessionAsync(tenantId, "team", seats: null, trialDays: null);

        captured!.LineItems.Should().HaveCount(1);
        captured.SubscriptionData.Should().BeNull();
    }

    [Test]
    public async Task Checkout_Keys_Differ_When_Params_Differ_But_Dedup_When_Same()
    {
        // Two checkouts for the SAME plan but DIFFERENT params (seats/trialDays) must
        // get DISTINCT keys — a same-key replay with different params 502s at Stripe.
        // A genuine retry of the SAME params dedups. Before the fix the key was only
        // (tenant, planSlug), so different-param checkouts COLLIDED.
        var h = SubscriptionHarness.Create(nameof(Checkout_Keys_Differ_When_Params_Differ_But_Dedup_When_Same));
        var tenantId = Guid.NewGuid();
        h.SeedCustomer(tenantId, "cus_x");
        h.SeedCatalog("team", "price_team", "price_seats");

        var keys = new List<string?>();
        h.Checkout.Setup(c => c.CreateAsync(
                It.IsAny<Stripe.Checkout.SessionCreateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stripe.Checkout.SessionCreateOptions, Stripe.RequestOptions, CancellationToken>(
                (_, ro, _) => keys.Add(ro.IdempotencyKey))
            .ReturnsAsync(new Stripe.Checkout.Session { Id = "cs", Url = "https://checkout/cs" });

        await h.Service.CreateCheckoutSessionAsync(tenantId, "team", seats: 3, trialDays: 14);
        await h.Service.CreateCheckoutSessionAsync(tenantId, "team", seats: 5, trialDays: 14);
        await h.Service.CreateCheckoutSessionAsync(tenantId, "team", seats: 3, trialDays: 14);

        keys[0].Should().Be($"sub-checkout-{tenantId:D}-team-s3-t14");
        keys[1].Should().Be($"sub-checkout-{tenantId:D}-team-s5-t14");
        keys[0].Should().NotBe(keys[1], "different seats ⇒ different key (else Stripe 502s on param mismatch)");
        keys[2].Should().Be(keys[0], "identical params dedup");
    }

    [Test]
    public void Checkout_Without_Customer_Mapping_Throws()
    {
        var h = SubscriptionHarness.Create(nameof(Checkout_Without_Customer_Mapping_Throws));
        var tenantId = Guid.NewGuid();
        h.SeedCatalog("team", "price_team");

        var act = async () => await h.Service.CreateCheckoutSessionAsync(tenantId, "team", null, null);
        act.Should().ThrowAsync<Tamma.Core.TammaError>()
            .Where(e => e.Code == Tamma.Api.Services.Billing.SubscriptionService.NoCustomerCode);
    }
}
