using System.Text.Json;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing.Handlers;

/// <summary>
/// Story 35-5 — default handler for the invoice lifecycle. Emits the canonical
/// <c>BILLING.INVOICE.*</c> DCB event per Stripe invoice event. The
/// <c>BillingInvoice</c> mirror + dunning are owned by Story 35-8. A
/// <c>invoice.payment_failed</c> returns a <see cref="BillingFollowup"/> so the
/// (heavy) dunning escalation runs async on the <c>billing.webhook.followup</c>
/// queue rather than inline (fast-ack, AC10); <c>paid</c>/<c>created</c>/
/// <c>finalized</c> need no follow-up.
/// </summary>
public sealed class InvoiceWebhookHandler : IBillingEventHandler
{
    private readonly IEventRepository _events;

    public InvoiceWebhookHandler(IEventRepository events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
    }

    public IReadOnlyCollection<string> HandledEventTypes => new[]
    {
        BillingWebhookEventTypes.InvoiceCreated,
        BillingWebhookEventTypes.InvoiceFinalized,
        BillingWebhookEventTypes.InvoicePaid,
        BillingWebhookEventTypes.InvoicePaymentFailed,
    };

    public async Task<BillingFollowup?> HandleAsync(BillingWebhookContext ctx, CancellationToken ct)
    {
        var dcbType = ctx.EventType switch
        {
            BillingWebhookEventTypes.InvoiceCreated => BillingWebhookEventTypes.DcbInvoiceCreated,
            BillingWebhookEventTypes.InvoiceFinalized => BillingWebhookEventTypes.DcbInvoiceFinalized,
            BillingWebhookEventTypes.InvoicePaid => BillingWebhookEventTypes.DcbInvoicePaid,
            BillingWebhookEventTypes.InvoicePaymentFailed => BillingWebhookEventTypes.DcbInvoicePaymentFailed,
            _ => throw new InvalidOperationException(
                $"InvoiceWebhookHandler received unclaimed type '{ctx.EventType}'."),
        };

        await _events.AppendAsync(BillingWebhookDcbEvents.Projection(dcbType, ctx))
            .ConfigureAwait(false);

        // Dunning escalation is heavy (Stripe round-trips, email) — Story 35-8
        // owns the actual work; 35-5 only enqueues the fast-ack follow-up so
        // recovery never runs inline (AC10).
        if (ctx.EventType == BillingWebhookEventTypes.InvoicePaymentFailed)
        {
            return new BillingFollowup(
                "invoice.payment_failed",
                JsonSerializer.Serialize(new
                {
                    reason = "invoice_payment_failed",
                    stripeEventId = ctx.StripeEventId,
                    stripeObjectId = ctx.StripeObjectId,
                }));
        }

        return null;
    }
}
