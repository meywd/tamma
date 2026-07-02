using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-4 (AC4) — <c>CancelAsync</c>: at-period-end keeps status
/// <c>active</c> + sets <c>CancelAtPeriodEnd</c>; immediate flips to
/// <c>canceled</c> and recomputes <c>Tenant.Plan</c> to free now.
/// </summary>
[TestFixture]
public class SubscriptionServiceCancelTests
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Cancel_AtPeriodEnd_Keeps_Active_And_Sets_Flag()
    {
        var h = SubscriptionHarness.Create(nameof(Cancel_AtPeriodEnd_Keeps_Active_And_Sets_Flag));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", periodEnd: End);

        Stripe.SubscriptionUpdateOptions? captured = null;
        h.Subscriptions.Setup(s => s.UpdateAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionUpdateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stripe.SubscriptionUpdateOptions, Stripe.RequestOptions, CancellationToken>(
                (_, o, _, _) => captured = o)
            .ReturnsAsync(SubscriptionHarness.MakeSub(
                "sub_1", "active", "price_team", Start, End, cancelAtPeriodEnd: true));

        var projection = await h.Service.CancelAsync(tenantId, atPeriodEnd: true);

        captured!.CancelAtPeriodEnd.Should().Be(true);
        projection.Status.Should().Be("active");
        projection.CancelAtPeriodEnd.Should().BeTrue();
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("team");
        h.Emitted.Should().Contain(e =>
            e.Type == Tamma.Api.Services.Billing.BillingEvents.SubscriptionCanceledType);
    }

    [Test]
    public async Task Cancel_Immediate_Flips_To_Canceled_And_Free()
    {
        var h = SubscriptionHarness.Create(nameof(Cancel_Immediate_Flips_To_Canceled_And_Free));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("free", 0m);
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", periodEnd: End);

        h.Subscriptions.Setup(s => s.CancelAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionCancelOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubscriptionHarness.MakeSub("sub_1", "canceled", "price_team", Start, End));

        var projection = await h.Service.CancelAsync(tenantId, atPeriodEnd: false);

        projection.Status.Should().Be("canceled");
        projection.PlanSlug.Should().Be("free");
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("free");
    }
}
