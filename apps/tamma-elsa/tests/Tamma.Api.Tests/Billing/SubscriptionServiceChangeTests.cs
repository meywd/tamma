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
        capturedRo!.IdempotencyKey.Should().Be(
            $"sub-change-{tenantId:D}-team-to-enterprise-{End:yyyyMMdd}");

        projection.PlanSlug.Should().Be("enterprise");
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("enterprise");
        h.Emitted.Should().Contain(e => e.Type == Tamma.Api.Services.Billing.BillingEvents.SubscriptionUpdatedType);
    }

    [Test]
    public async Task Downgrade_Schedules_TwoPhase_With_Target_Price_And_Leaves_Live_Plan_Unchanged()
    {
        var h = SubscriptionHarness.Create(
            nameof(Downgrade_Schedules_TwoPhase_With_Target_Price_And_Leaves_Live_Plan_Unchanged));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("free", 0m);
        h.SeedPlan("team", 50m);
        h.SeedCatalog("free", "price_free");
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", periodEnd: End);

        // A from_subscription create returns a single-phase schedule mirroring the
        // CURRENT (team) sub through the current period end.
        Stripe.SubscriptionScheduleCreateOptions? createOpts = null;
        Stripe.RequestOptions? createRo = null;
        h.Schedules.Setup(s => s.CreateAsync(
                It.IsAny<Stripe.SubscriptionScheduleCreateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Stripe.SubscriptionScheduleCreateOptions, Stripe.RequestOptions, CancellationToken>(
                (o, ro, _) => { createOpts = o; createRo = ro; })
            .ReturnsAsync(SubscriptionHarness.MakeSchedule("sub_sched_1", "price_team", Start, End));

        // The real scheduled downgrade is the UPDATE: two phases, phase 2 carrying
        // the TARGET (free) price id.
        Stripe.SubscriptionScheduleUpdateOptions? updateOpts = null;
        Stripe.RequestOptions? updateRo = null;
        h.Schedules.Setup(s => s.UpdateAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionScheduleUpdateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stripe.SubscriptionScheduleUpdateOptions, Stripe.RequestOptions, CancellationToken>(
                (_, o, ro, _) => { updateOpts = o; updateRo = ro; })
            .ReturnsAsync(SubscriptionHarness.MakeSchedule("sub_sched_1", "price_free", Start, End));

        var projection = await h.Service.ChangePlanAsync(tenantId, "free");

        createOpts!.FromSubscription.Should().Be("sub_1");
        createRo!.IdempotencyKey.Should().Be($"sub-downgrade-{tenantId:D}-team-to-free-{End:yyyyMMdd}");

        // The schedule UPDATE is what actually applies the downgrade at period end.
        updateOpts.Should().NotBeNull();
        updateOpts!.EndBehavior.Should().Be("release");
        updateOpts.Phases.Should().HaveCount(2);
        // Phase 1 = the current (team) price through the current period end.
        updateOpts.Phases[0].Items.Should().ContainSingle();
        updateOpts.Phases[0].Items[0].Price.Should().Be("price_team");
        // Phase 2 = the TARGET (free) price — the crux of the fix.
        updateOpts.Phases[1].Items.Should().ContainSingle();
        updateOpts.Phases[1].Items[0].Price.Should().Be("price_free");
        updateRo!.IdempotencyKey.Should().Be(
            $"sub-downgrade-update-{tenantId:D}-team-to-free-{End:yyyyMMdd}");

        // Live plan / Tenant.Plan stay on team until the rollover webhook (AC3).
        projection.ScheduledPlanSlug.Should().Be("free");
        projection.PlanSlug.Should().Be("team");
        var mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.PlanSlug.Should().Be("team");
        mirror.StripeScheduleId.Should().Be("sub_sched_1");
        mirror.ScheduledEffectiveAt.Should().Be(End);
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("team");

        // No immediate subscription UPDATE on a downgrade (only the schedule moves).
        h.Subscriptions.Verify(s => s.UpdateAsync(
            It.IsAny<string>(), It.IsAny<Stripe.SubscriptionUpdateOptions>(),
            It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Plan_Change_Keys_Distinguish_Source_Slug_But_Dedup_Same_Intent()
    {
        // Two DISTINCT upgrade intents to the SAME target (enterprise) from DIFFERENT
        // sources (team vs team2) must get DISTINCT keys — otherwise Stripe replays
        // the first and never re-applies the second (drift). A genuine retry of the
        // SAME source→target dedups. Before the fix the key was keyed only on the
        // TARGET slug, so team→enterprise and team2→enterprise COLLIDED.
        var h = SubscriptionHarness.Create(
            nameof(Plan_Change_Keys_Distinguish_Source_Slug_But_Dedup_Same_Intent));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedPlan("team2", 50m);
        h.SeedPlan("enterprise", 100m);
        h.SeedCatalog("team", "price_team");
        h.SeedCatalog("team2", "price_team2");
        h.SeedCatalog("enterprise", "price_enterprise");
        h.SeedTenant(tenantId, plan: "team");
        var mirror = h.SeedMirror(tenantId, "team", "sub_1", periodEnd: End);

        h.Subscriptions.Setup(s => s.GetAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionGetOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubscriptionHarness.MakeSub("sub_1", "active", "price_team", Start, End));

        var keys = new List<string?>();
        h.Subscriptions.Setup(s => s.UpdateAsync(
                It.IsAny<string>(), It.IsAny<Stripe.SubscriptionUpdateOptions>(),
                It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stripe.SubscriptionUpdateOptions, Stripe.RequestOptions, CancellationToken>(
                (_, o, ro, _) => keys.Add(ro.IdempotencyKey))
            .ReturnsAsync((string _, Stripe.SubscriptionUpdateOptions o, Stripe.RequestOptions _, CancellationToken _) =>
                SubscriptionHarness.MakeSub("sub_1", "active", o.Items[0].Price, Start, End));

        // team → enterprise
        await h.Service.ChangePlanAsync(tenantId, "enterprise");
        // Reset the source to team2, then team2 → enterprise (SAME target, DIFFERENT source).
        mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.PlanSlug = "team2";
        await h.Db.SaveChangesAsync();
        await h.Service.ChangePlanAsync(tenantId, "enterprise");
        // Reset the source back to team, retry team → enterprise (SAME intent as call 1).
        mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.PlanSlug = "team";
        await h.Db.SaveChangesAsync();
        await h.Service.ChangePlanAsync(tenantId, "enterprise");

        keys.Should().HaveCount(3);
        keys[0].Should().Be($"sub-change-{tenantId:D}-team-to-enterprise-{End:yyyyMMdd}");
        keys[1].Should().Be($"sub-change-{tenantId:D}-team2-to-enterprise-{End:yyyyMMdd}");
        keys[0].Should().NotBe(keys[1],
            "distinct source→same-target intents must get distinct keys (else Stripe replays)");
        keys[2].Should().Be(keys[0], "a genuine retry of the SAME source→target dedups");
    }
}
