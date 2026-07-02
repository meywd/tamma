using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-4 (AC3, AC7, AC10) — <c>ChangePlanAsync</c>: an upgrade updates the
/// Stripe subscription with immediate proration and applies the new slug now; a
/// downgrade creates a Subscription Schedule, records the scheduled fields, and
/// leaves the live plan / <c>Tenant.Plan</c> at the current (higher) plan.
/// </summary>
[TestFixture]
public class SubscriptionServiceChangeTests
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Upgrade_Prorates_And_Applies_New_Slug_Now()
    {
        var h = SubscriptionHarness.Create(nameof(Upgrade_Prorates_And_Applies_New_Slug_Now));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedPlan("enterprise", 100m);
        h.SeedCatalog("team", "price_team");
        h.SeedCatalog("enterprise", "price_enterprise");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", periodEnd: End);

        h.Subscriptions.Setup(s => s.GetAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionGetOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubscriptionHarness.MakeSub("sub_1", "active", "price_team", Start, End));

        Stripe.SubscriptionUpdateOptions? captured = null;
        Stripe.RequestOptions? capturedRo = null;
        h.Subscriptions.Setup(s => s.UpdateAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionUpdateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stripe.SubscriptionUpdateOptions, Stripe.RequestOptions, CancellationToken>(
                (_, o, ro, _) => { captured = o; capturedRo = ro; })
            .ReturnsAsync(SubscriptionHarness.MakeSub("sub_1", "active", "price_enterprise", Start, End));

        var projection = await h.Service.ChangePlanAsync(tenantId, "enterprise");

        captured!.ProrationBehavior.Should().Be("create_prorations");
        captured.Items.Should().ContainSingle();
        captured.Items[0].Id.Should().Be("si_base");
        captured.Items[0].Price.Should().Be("price_enterprise");
        capturedRo!.IdempotencyKey.Should().Be($"sub-change-{tenantId:D}-enterprise-{End:yyyyMMdd}");

        projection.PlanSlug.Should().Be("enterprise");
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("enterprise");
        h.Emitted.Should().Contain(e => e.Type == Tamma.Api.Services.Billing.BillingEvents.SubscriptionUpdatedType);
    }

    [Test]
    public async Task Downgrade_Schedules_And_Leaves_Live_Plan_Unchanged()
    {
        var h = SubscriptionHarness.Create(nameof(Downgrade_Schedules_And_Leaves_Live_Plan_Unchanged));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("free", 0m);
        h.SeedPlan("team", 50m);
        h.SeedCatalog("free", "price_free");
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", periodEnd: End);

        Stripe.SubscriptionScheduleCreateOptions? captured = null;
        Stripe.RequestOptions? capturedRo = null;
        h.Schedules.Setup(s => s.CreateAsync(
                It.IsAny<Stripe.SubscriptionScheduleCreateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stripe.SubscriptionScheduleCreateOptions, Stripe.RequestOptions, CancellationToken>(
                (o, ro, _) => { captured = o; capturedRo = ro; })
            .ReturnsAsync(new Stripe.SubscriptionSchedule { Id = "sub_sched_1" });

        var projection = await h.Service.ChangePlanAsync(tenantId, "free");

        captured!.FromSubscription.Should().Be("sub_1");
        capturedRo!.IdempotencyKey.Should().Be($"sub-downgrade-{tenantId:D}-free-{End:yyyyMMdd}");

        projection.ScheduledPlanSlug.Should().Be("free");
        projection.PlanSlug.Should().Be("team");
        var mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.PlanSlug.Should().Be("team");
        mirror.StripeScheduleId.Should().Be("sub_sched_1");
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("team");

        // No immediate subscription update on a downgrade.
        h.Subscriptions.Verify(s => s.UpdateAsync(
            It.IsAny<string>(), It.IsAny<Stripe.SubscriptionUpdateOptions>(),
            It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
