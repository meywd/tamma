using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing.Handlers;

/// <summary>
/// Story 35-5 — default handler for payment-intent lifecycle. Emits the
/// canonical <c>BILLING.PAYMENT.*</c> DCB event per Stripe payment_intent event.
/// The <c>BillingPaymentMethod</c> mirror + portal are owned by Story 35-7. No
/// follow-up: a failed payment_intent is surfaced through the DCB trail and (for
/// invoices) the invoice.payment_failed dunning follow-up — a payment_intent
/// failure alone needs no async escalation here.
/// </summary>
public sealed class PaymentWebhookHandler : IBillingEventHandler
{
    private readonly IEventRepository _events;

    public PaymentWebhookHandler(IEventRepository events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
    }

    public IReadOnlyCollection<string> HandledEventTypes => new[]
    {
        BillingWebhookEventTypes.PaymentIntentSucceeded,
        BillingWebhookEventTypes.PaymentIntentPaymentFailed,
    };

    public async Task<BillingFollowup?> HandleAsync(BillingWebhookContext ctx, CancellationToken ct)
    {
        var dcbType = ctx.EventType switch
        {
            BillingWebhookEventTypes.PaymentIntentSucceeded => BillingWebhookEventTypes.DcbPaymentSucceeded,
            BillingWebhookEventTypes.PaymentIntentPaymentFailed => BillingWebhookEventTypes.DcbPaymentFailed,
            _ => throw new InvalidOperationException(
                $"PaymentWebhookHandler received unclaimed type '{ctx.EventType}'."),
        };

        await _events.AppendAsync(BillingWebhookDcbEvents.Projection(dcbType, ctx))
            .ConfigureAwait(false);
        return null;
    }
}
