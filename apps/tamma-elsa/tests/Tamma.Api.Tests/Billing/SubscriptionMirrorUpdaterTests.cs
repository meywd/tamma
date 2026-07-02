using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Services.Billing;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-4 (AC5, AC7, AC8, AC13) — the shared <see cref="SubscriptionMirrorUpdater"/>:
/// applies a Stripe object onto the mirror + <c>Tenant.Plan</c>/<c>PlanId</c>
/// lockstep (no drift), takes status/period from the Stripe object (not the
/// request), and emits exactly one <c>BILLING.SUBSCRIPTION.*</c> event per
/// transition. A scheduled downgrade never touches the live plan.
/// </summary>
[TestFixture]
public class SubscriptionMirrorUpdaterTests
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Apply_Created_Materializes_Mirror_And_Lockstep()
    {
        var h = SubscriptionHarness.Create(nameof(Apply_Created_Materializes_Mirror_And_Lockstep));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("free", 0m);
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "free");

        var sub = SubscriptionHarness.MakeSub("sub_1", "active", "price_team", Start, End);
        var projection = await h.Updater.ApplyAsync(
            tenantId, sub, SubscriptionMirrorUpdater.TransitionCreated);

        projection.PlanSlug.Should().Be("team");
        projection.Status.Should().Be("active");

        var mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.StripeSubscriptionId.Should().Be("sub_1");
        mirror.PlanSlug.Should().Be("team");
        mirror.Status.Should().Be("active");
        mirror.CurrentPeriodStart.Should().Be(Start);
        mirror.CurrentPeriodEnd.Should().Be(End);

        // No-drift lockstep (AC7).
        var tenant = await h.Db.Tenants.SingleAsync();
        tenant.Plan.Should().Be("team");
        var planId = (Guid?)h.Db.Entry(tenant).Property("PlanId").CurrentValue;
        var teamPlanId = (await h.Db.Plans.SingleAsync(p => p.Slug == "team")).Id;
        planId.Should().Be(teamPlanId);

        h.Emitted.Should().ContainSingle(e =>
            e.Type == BillingEvents.SubscriptionCreatedType && e.TenantId == tenantId);
    }

    [Test]
    public async Task Apply_Takes_Status_And_Period_From_Stripe_Not_Mirror()
    {
        var h = SubscriptionHarness.Create(nameof(Apply_Takes_Status_And_Period_From_Stripe_Not_Mirror));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", status: "active");

        // Stripe now reports past_due with a NEW period — AC13: the mirror must
        // reflect Stripe's confirmed state, never a stale request-side value.
        var sub = SubscriptionHarness.MakeSub("sub_1", "past_due", "price_team", Start, End);
        await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionUpdated);

        var mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.Status.Should().Be("past_due");
        mirror.CurrentPeriodEnd.Should().Be(End);
    }

    [Test]
    public async Task Apply_Immediate_Cancel_Sets_Free_And_Emits_Canceled()
    {
        var h = SubscriptionHarness.Create(nameof(Apply_Immediate_Cancel_Sets_Free_And_Emits_Canceled));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("free", 0m);
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1");

        var sub = SubscriptionHarness.MakeSub("sub_1", "canceled", "price_team", Start, End);
        await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionCanceled);

        var mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.Status.Should().Be("canceled");
        mirror.PlanSlug.Should().Be("free");
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("free");
        h.Emitted.Should().ContainSingle(e =>
            e.Type == BillingEvents.SubscriptionCanceledType && e.TenantId == tenantId);
    }

    [Test]
    public async Task Apply_TrialEnded_Conversion_Emits_TrialEnded_Active()
    {
        var h = SubscriptionHarness.Create(nameof(Apply_TrialEnded_Conversion_Emits_TrialEnded_Active));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", status: "trialing");

        var sub = SubscriptionHarness.MakeSub("sub_1", "active", "price_team", Start, End);
        await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionTrialEnded);

        var mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.Status.Should().Be("active");
        h.Emitted.Should().ContainSingle(e =>
            e.Type == BillingEvents.SubscriptionTrialEndedType && e.TenantId == tenantId);
    }

    [Test]
    public async Task Apply_TrialEnded_Expiry_Falls_To_Free()
    {
        var h = SubscriptionHarness.Create(nameof(Apply_TrialEnded_Expiry_Falls_To_Free));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("free", 0m);
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", status: "trialing");

        var sub = SubscriptionHarness.MakeSub("sub_1", "unpaid", "price_team", Start, End);
        await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionTrialEnded);

        var mirror = await h.Db.BillingSubscriptions.SingleAsync();
        mirror.Status.Should().Be("unpaid");
        mirror.PlanSlug.Should().Be("free");
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("free");
    }

    [Test]
    public async Task RecordScheduledDowngrade_Leaves_Live_Plan_Unchanged()
    {
        var h = SubscriptionHarness.Create(nameof(RecordScheduledDowngrade_Leaves_Live_Plan_Unchanged));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("free", 0m);
        h.SeedPlan("team", 50m);
        h.SeedTenant(tenantId, plan: "team");
        var mirror = h.SeedMirror(tenantId, "team", "sub_1");
        var effectiveAt = mirror.CurrentPeriodEnd;

        var projection = await h.Updater.RecordScheduledDowngradeAsync(
            mirror, "free", effectiveAt, "sub_sched_1");

        projection.ScheduledPlanSlug.Should().Be("free");
        projection.PlanSlug.Should().Be("team", "the live plan stays until the rollover webhook (AC3)");

        var reloaded = await h.Db.BillingSubscriptions.SingleAsync();
        reloaded.ScheduledPlanSlug.Should().Be("free");
        reloaded.ScheduledEffectiveAt.Should().Be(effectiveAt);
        reloaded.StripeScheduleId.Should().Be("sub_sched_1");
        reloaded.PlanSlug.Should().Be("team");
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("team");

        h.Emitted.Should().ContainSingle(e =>
            e.Type == BillingEvents.SubscriptionUpdatedType && e.Data.Contains("free"));
    }

    [Test]
    public async Task Apply_Emits_One_Event_With_Tenant_Tags()
    {
        var h = SubscriptionHarness.Create(nameof(Apply_Emits_One_Event_With_Tenant_Tags));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1");

        var sub = SubscriptionHarness.MakeSub("sub_1", "active", "price_team", Start, End);
        await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionUpgraded);

        h.Emitted.Should().HaveCount(1);
        var evt = h.Emitted[0];
        evt.Type.Should().Be(BillingEvents.SubscriptionUpdatedType);
        evt.TenantId.Should().Be(tenantId);
        evt.Tags.Should().Contain("team").And.Contain("active");
    }
}
