namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-4 — the subscription webhook handler that SUPERSEDES the 35-5
/// audit-only <c>SubscriptionWebhookHandler</c> (whose own doc anticipates this:
/// "the <c>BillingSubscription</c> mirror is owned by Story 35-4 … 35-4
/// supersedes this claim once it lands"). Registered in place of it so the
/// registry's duplicate-claim guard is satisfied (exactly one handler per Stripe
/// type).
///
/// <para>It drives the SHARED <see cref="SubscriptionMirrorUpdater"/> — the same
/// entry point the API path uses — so a webhook reconciles the mirror +
/// <c>Tenant.Plan</c> lockstep + emits the <c>BILLING.SUBSCRIPTION.*</c> DCB
/// event without re-implementing any of it. No follow-up work (the updater is
/// synchronous and cheap).</para>
/// </summary>
public sealed class SubscriptionMirrorWebhookHandler : IBillingEventHandler
{
    private readonly SubscriptionMirrorUpdater _mirror;

    public SubscriptionMirrorWebhookHandler(SubscriptionMirrorUpdater mirror)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        _mirror = mirror;
    }

    public IReadOnlyCollection<string> HandledEventTypes => new[]
    {
        BillingWebhookEventTypes.SubscriptionCreated,
        BillingWebhookEventTypes.SubscriptionUpdated,
        BillingWebhookEventTypes.SubscriptionDeleted,
        BillingWebhookEventTypes.SubscriptionTrialWillEnd,
    };

    public async Task<BillingFollowup?> HandleAsync(BillingWebhookContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.StripeEvent.Data?.Object is not Stripe.Subscription stripeSub)
        {
            throw new InvalidOperationException(
                $"SubscriptionMirrorWebhookHandler could not read a Stripe.Subscription from "
                + $"event '{ctx.StripeEventId}' ({ctx.EventType}).");
        }

        var transition = ctx.EventType switch
        {
            BillingWebhookEventTypes.SubscriptionCreated => SubscriptionMirrorUpdater.TransitionCreated,
            BillingWebhookEventTypes.SubscriptionUpdated => SubscriptionMirrorUpdater.TransitionUpdated,
            BillingWebhookEventTypes.SubscriptionDeleted => SubscriptionMirrorUpdater.TransitionCanceled,
            BillingWebhookEventTypes.SubscriptionTrialWillEnd =>
                SubscriptionMirrorUpdater.TransitionTrialWillEnd,
            _ => throw new InvalidOperationException(
                $"SubscriptionMirrorWebhookHandler received unclaimed type '{ctx.EventType}'."),
        };

        await _mirror.ApplyAsync(ctx.TenantId, stripeSub, transition, ct).ConfigureAwait(false);
        return null;
    }
}
