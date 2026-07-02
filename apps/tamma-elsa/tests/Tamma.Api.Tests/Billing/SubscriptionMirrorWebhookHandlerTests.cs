using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Billing;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-4 (Finding 4) — the registered <see cref="SubscriptionMirrorWebhookHandler"/>
/// (which supersedes 35-5's audit-only handler) maps the Stripe
/// <c>customer.subscription.trial_will_end</c> event to the semantically-correct
/// <c>BILLING.SUBSCRIPTION.TRIAL_ENDING</c> DCB type — that Stripe event fires
/// BEFORE the trial ends (the sub is still <c>trialing</c>), so it must not be
/// labelled <c>..._ENDED</c>.
/// </summary>
[TestFixture]
public class SubscriptionMirrorWebhookHandlerTests
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task TrialWillEnd_Webhook_Emits_TrialEnding()
    {
        var h = SubscriptionHarness.Create(nameof(TrialWillEnd_Webhook_Emits_TrialEnding));
        var tenantId = Guid.NewGuid();
        h.SeedPlan("team", 50m);
        h.SeedCatalog("team", "price_team");
        h.SeedTenant(tenantId, plan: "team");
        h.SeedMirror(tenantId, "team", "sub_1", status: "trialing", periodEnd: End);

        var sub = SubscriptionHarness.MakeSub("sub_1", "trialing", "price_team", Start, End);
        var evt = new Stripe.Event
        {
            Id = "evt_twe",
            Type = BillingWebhookEventTypes.SubscriptionTrialWillEnd,
            Data = new Stripe.EventData { Object = sub },
        };
        var ctx = new BillingWebhookContext(
            evt, tenantId, BillingWebhookEventTypes.SubscriptionTrialWillEnd,
            "evt_twe", "sub_1", "{}");

        var handler = new SubscriptionMirrorWebhookHandler(h.Updater);
        var followup = await handler.HandleAsync(ctx, CancellationToken.None);

        followup.Should().BeNull();
        h.Emitted.Should().Contain(e => e.Type == BillingEvents.SubscriptionTrialEndingType);
        h.Emitted.Should().NotContain(e => e.Type == "BILLING.SUBSCRIPTION.TRIAL_ENDED");
    }
}
