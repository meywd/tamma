using System.Text.Json;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing.Handlers;

/// <summary>
/// Story 35-5 — default handler for <c>charge.dispute.created</c>. Emits
/// <c>BILLING.DISPUTE.OPENED</c> and returns a <see cref="BillingFollowup"/> so
/// the (heavy) dispute-response workflow — evidence gathering, notification —
/// runs async on the <c>billing.webhook.followup</c> queue rather than inline
/// (fast-ack, AC10). A dispute is a platform-operator concern; the mirror/response
/// automation is owned by a later story.
/// </summary>
public sealed class DisputeWebhookHandler : IBillingEventHandler
{
    private readonly IEventRepository _events;

    public DisputeWebhookHandler(IEventRepository events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
    }

    public IReadOnlyCollection<string> HandledEventTypes => new[]
    {
        BillingWebhookEventTypes.ChargeDisputeCreated,
    };

    public async Task<BillingFollowup?> HandleAsync(BillingWebhookContext ctx, CancellationToken ct)
    {
        await _events.AppendAsync(
            BillingWebhookDcbEvents.Projection(BillingWebhookEventTypes.DcbDisputeOpened, ctx))
            .ConfigureAwait(false);

        return new BillingFollowup(
            "charge.dispute.created",
            JsonSerializer.Serialize(new
            {
                reason = "dispute_opened",
                stripeEventId = ctx.StripeEventId,
                stripeObjectId = ctx.StripeObjectId,
            }));
    }
}
