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
    public async Task Apply_TrialWillEnd_Emits_TrialEnding_Not_TrialEnded()
    {
        // trial_will_end fires BEFORE the trial ends → the DCB type must be the
        // semantically-correct TRIAL_ENDING (matching Story 35-5's name), NOT
        // TRIAL_ENDED (which would orphan any consumer on the 35-5 string).
        var h = SubscriptionHarness.Create(nameof(Apply_TrialWillEnd_Emits_TrialEnding_Not_TrialEnded));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        // Still trialing when trial_will_end fires (3 days out).
        h.SeedMirror(tenantId, "team", "sub_1", status: "active");

        var sub = SubscriptionHarness.MakeSub("sub_1", "trialing", "price_team", Start, End);
        await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionTrialWillEnd);

        h.Emitted.Should().ContainSingle(e =>
            e.Type == BillingEvents.SubscriptionTrialEndingType && e.TenantId == tenantId);
        h.Emitted.Should().NotContain(e => e.Type == "BILLING.SUBSCRIPTION.TRIAL_ENDED");
        BillingEvents.SubscriptionTrialEndingType.Should().Be("BILLING.SUBSCRIPTION.TRIAL_ENDING");
    }

    [Test]
    public async Task Apply_Terminal_Trial_Expiry_Falls_To_Free()
    {
        var h = SubscriptionHarness.Create(nameof(Apply_Terminal_Trial_Expiry_Falls_To_Free));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("free", 0m);
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", status: "trialing");

        var sub = SubscriptionHarness.MakeSub("sub_1", "unpaid", "price_team", Start, End);
        await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionCanceled);

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

    [Test]
    public async Task Apply_Same_Stripe_State_Twice_Emits_Exactly_One_Event()
    {
        // An API-initiated change applies once (emits), then the resulting Stripe
        // webhook re-applies the SAME state. The second apply must NOT emit a
        // duplicate DCB event (Epic 36/37 double-count) — emit only on real change.
        var h = SubscriptionHarness.Create(nameof(Apply_Same_Stripe_State_Twice_Emits_Exactly_One_Event));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", status: "active");

        var sub = SubscriptionHarness.MakeSub("sub_1", "past_due", "price_team", Start, End);

        // First apply changes the mirror (status active→past_due, new period) → emits.
        await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionUpdated);
        h.Emitted.Should().HaveCount(1);

        // Second apply of the IDENTICAL Stripe state is a pure replay → no emit.
        await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionUpdated);
        h.Emitted.Should().HaveCount(1, "applying the same Stripe state twice emits exactly one event");
    }

    [Test]
    public async Task Apply_Rollover_To_Scheduled_Target_Clears_Pending_Downgrade()
    {
        // When the schedule rolls over, Stripe fires customer.subscription.updated
        // with the TARGET (free) price. ApplyAsync resolves effectiveSlug == free ==
        // ScheduledPlanSlug and clears the pending-downgrade fields.
        var h = SubscriptionHarness.Create(nameof(Apply_Rollover_To_Scheduled_Target_Clears_Pending_Downgrade));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("free", 0m);
        h.SeedPlan("team", 50m);
        h.SeedCatalog("free", "price_free");
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        var mirror = h.SeedMirror(tenantId, "team", "sub_1", periodEnd: End);
        mirror.ScheduledPlanSlug = "free";
        mirror.ScheduledEffectiveAt = End;
        mirror.StripeScheduleId = "sub_sched_1";
        await h.Db.SaveChangesAsync();

        // Rollover: the sub's base price is now the target (free) price.
        var sub = SubscriptionHarness.MakeSub("sub_1", "active", "price_free", Start, End);
        await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionUpdated);

        var reloaded = await h.Db.BillingSubscriptions.SingleAsync();
        reloaded.PlanSlug.Should().Be("free");
        reloaded.ScheduledPlanSlug.Should().BeNull("the pending downgrade has rolled over");
        reloaded.ScheduledEffectiveAt.Should().BeNull();
        reloaded.StripeScheduleId.Should().BeNull();
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("free");
    }

    [Test]
    public async Task Apply_Missing_Target_Plan_Row_Throws_Rather_Than_Drifting()
    {
        // Fail LOUD (Finding 5): a cancel→free with no seeded `free` Plan row must
        // throw, not silently leave Tenant.Plan on the old higher plan.
        var h = SubscriptionHarness.Create(nameof(Apply_Missing_Target_Plan_Row_Throws_Rather_Than_Drifting));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1");
        // NB: no "free" plan seeded → the free-fallback lockstep has no target row.

        var sub = SubscriptionHarness.MakeSub("sub_1", "canceled", "price_team", Start, End);
        var act = async () =>
            await h.Updater.ApplyAsync(tenantId, sub, SubscriptionMirrorUpdater.TransitionCanceled);

        (await act.Should().ThrowAsync<Tamma.Core.TammaError>())
            .Where(e => e.Code == SubscriptionMirrorUpdater.LockstepPlanMissingCode);

        // Tenant.Plan was NOT silently downgraded/left stale via a committed drift.
        (await h.Db.Tenants.SingleAsync()).Plan.Should().Be("team");
    }
}
