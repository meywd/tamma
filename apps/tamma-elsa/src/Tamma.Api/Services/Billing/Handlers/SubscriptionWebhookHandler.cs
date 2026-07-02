using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing.Handlers;

/// <summary>
/// Story 35-5 — default handler for the subscription lifecycle. Emits the
/// canonical <c>BILLING.SUBSCRIPTION.*</c> DCB event per Stripe subscription
/// event. The <c>BillingSubscription</c> mirror is owned by Story 35-4, which
/// registers its own <see cref="IBillingEventHandler"/> — 35-5 only guarantees
/// the DCB audit trail is complete from day one (35-4 supersedes this claim once
/// it lands, since duplicate-claim detection forbids both). No follow-up work.
/// </summary>
public sealed class SubscriptionWebhookHandler : IBillingEventHandler
{
    private readonly IEventRepository _events;

    public SubscriptionWebhookHandler(IEventRepository events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
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
        var dcbType = ctx.EventType switch
        {
            BillingWebhookEventTypes.SubscriptionCreated => BillingWebhookEventTypes.DcbSubscriptionCreated,
            BillingWebhookEventTypes.SubscriptionUpdated => BillingWebhookEventTypes.DcbSubscriptionUpdated,
            BillingWebhookEventTypes.SubscriptionDeleted => BillingWebhookEventTypes.DcbSubscriptionDeleted,
            BillingWebhookEventTypes.SubscriptionTrialWillEnd => BillingWebhookEventTypes.DcbSubscriptionTrialEnding,
            _ => throw new InvalidOperationException(
                $"SubscriptionWebhookHandler received unclaimed type '{ctx.EventType}'."),
        };

        await _events.AppendAsync(BillingWebhookDcbEvents.Projection(dcbType, ctx))
            .ConfigureAwait(false);
        return null;
    }
}
