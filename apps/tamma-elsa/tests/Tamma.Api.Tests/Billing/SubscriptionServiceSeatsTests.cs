using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-4 (AC6) — <c>ChangeSeatsAsync</c>: an increase updates the Stripe
/// seat quantity + the mirror <c>Seats</c>; a decrease below the tenant's active
/// membership count is rejected with <c>seats_below_active_members</c> BEFORE any
/// Stripe call.
/// </summary>
[TestFixture]
public class SubscriptionServiceSeatsTests
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Increase_Updates_Stripe_Quantity_And_Mirror_Seats()
    {
        var h = SubscriptionHarness.Create(nameof(Increase_Updates_Stripe_Quantity_And_Mirror_Seats));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team", "price_seats");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", seats: 1, periodEnd: End);
        h.SeedMembers(tenantId, 1);

        h.Subscriptions.Setup(s => s.GetAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionGetOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubscriptionHarness.MakeSub(
                "sub_1", "active", "price_team", Start, End,
                seatsPriceId: "price_seats", seatsQty: 1));

        Stripe.SubscriptionUpdateOptions? captured = null;
        h.Subscriptions.Setup(s => s.UpdateAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionUpdateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stripe.SubscriptionUpdateOptions, Stripe.RequestOptions, CancellationToken>(
                (_, o, _, _) => captured = o)
            .ReturnsAsync(SubscriptionHarness.MakeSub(
                "sub_1", "active", "price_team", Start, End,
                seatsPriceId: "price_seats", seatsQty: 5));

        var projection = await h.Service.ChangeSeatsAsync(tenantId, 5);

        captured!.Items.Should().ContainSingle();
        captured.Items[0].Id.Should().Be("si_seat");
        captured.Items[0].Quantity.Should().Be(5);
        projection.Seats.Should().Be(5);
        (await h.Db.BillingSubscriptions.SingleAsync()).Seats.Should().Be(5);
    }

    [Test]
    public async Task Decrease_Below_Active_Members_Rejected_With_No_Stripe_Call()
    {
        var h = SubscriptionHarness.Create(nameof(Decrease_Below_Active_Members_Rejected_With_No_Stripe_Call));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team", "price_seats");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", seats: 5, periodEnd: End);
        h.SeedMembers(tenantId, 3);

        var act = async () => await h.Service.ChangeSeatsAsync(tenantId, 2);

        (await act.Should().ThrowAsync<Tamma.Core.TammaError>())
            .Where(e => e.Code == Tamma.Api.Services.Billing.SubscriptionService.SeatsBelowActiveMembersCode);

        // Rejected before any Stripe call (AC6).
        h.Factory.Verify(f => f.CreateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Seats_Equal_To_Active_Members_Allowed()
    {
        var h = SubscriptionHarness.Create(nameof(Seats_Equal_To_Active_Members_Allowed));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team", "price_seats");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", seats: 5, periodEnd: End);
        h.SeedMembers(tenantId, 3);

        h.Subscriptions.Setup(s => s.GetAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionGetOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubscriptionHarness.MakeSub(
                "sub_1", "active", "price_team", Start, End,
                seatsPriceId: "price_seats", seatsQty: 5));
        h.Subscriptions.Setup(s => s.UpdateAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionUpdateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubscriptionHarness.MakeSub(
                "sub_1", "active", "price_team", Start, End,
                seatsPriceId: "price_seats", seatsQty: 3));

        var projection = await h.Service.ChangeSeatsAsync(tenantId, 3);
        projection.Seats.Should().Be(3);
    }

    [Test]
    public async Task Seat_Keys_Distinguish_From_Count_But_Dedup_Same_Intent()
    {
        // Two DISTINCT seat intents to the SAME target (10) from DIFFERENT current
        // counts (5 vs 8) must get DISTINCT keys — otherwise Stripe replays the
        // first cached response and never re-applies the second (Stripe drifts from
        // the mirror). A genuine retry of the SAME from→to dedups. Before the fix
        // the key was keyed only on the TARGET count, so 5→10 and 8→10 COLLIDED.
        var h = SubscriptionHarness.Create(nameof(Seat_Keys_Distinguish_From_Count_But_Dedup_Same_Intent));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team", "price_seats");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", seats: 5, periodEnd: End);
        // No SeedMembers → 0 active members → every decrease is allowed.

        h.Subscriptions.Setup(s => s.GetAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionGetOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubscriptionHarness.MakeSub(
                "sub_1", "active", "price_team", Start, End,
                seatsPriceId: "price_seats", seatsQty: 5));

        var keys = new List<string?>();
        h.Subscriptions.Setup(s => s.UpdateAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionUpdateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stripe.SubscriptionUpdateOptions, Stripe.RequestOptions, CancellationToken>(
                (_, o, ro, _) => keys.Add(ro.IdempotencyKey))
            .ReturnsAsync((string _, Stripe.SubscriptionUpdateOptions o, Stripe.RequestOptions _, CancellationToken _) =>
                SubscriptionHarness.MakeSub(
                    "sub_1", "active", "price_team", Start, End,
                    seatsPriceId: "price_seats", seatsQty: o.Items[0].Quantity ?? 0));

        await h.Service.ChangeSeatsAsync(tenantId, 10); // 5 → 10 (mirror advances to 10)
        // Reset the current count to 8, then 8 → 10 (SAME target, DIFFERENT source).
        var mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.Seats = 8;
        await h.Db.SaveChangesAsync();
        await h.Service.ChangeSeatsAsync(tenantId, 10);
        // Reset the current count back to 5, retry 5 → 10 (SAME intent as call 1).
        mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.Seats = 5;
        await h.Db.SaveChangesAsync();
        await h.Service.ChangeSeatsAsync(tenantId, 10);

        keys.Should().HaveCount(3);
        keys[0].Should().Be($"sub-seats-{tenantId:D}-5-to-10-{End:yyyyMMdd}");
        keys[1].Should().Be($"sub-seats-{tenantId:D}-8-to-10-{End:yyyyMMdd}");
        keys[0].Should().NotBe(keys[1],
            "distinct from→same-target seat intents must get distinct keys (else Stripe replays)");
        keys[2].Should().Be(keys[0], "a genuine retry of the SAME seat change dedups");
    }
}
