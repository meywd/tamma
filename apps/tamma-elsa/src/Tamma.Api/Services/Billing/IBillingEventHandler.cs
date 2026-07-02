namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 — pluggable projection handler for one or more Stripe event types.
/// 35-5 ships the dispatch seam + DCB-emitting default handlers; sibling stories
/// (35-4 subscription mirror, 35-7 payment-method mirror, 35-8 invoice/dunning
/// mirror, 35-10 wallet) register their own handlers that own their mirror
/// entities — 35-5 never creates <c>BillingSubscription</c>/<c>BillingInvoice</c>/
/// <c>BillingPaymentMethod</c>.
///
/// <para>Registered via <c>services.AddBillingEventHandler&lt;T&gt;()</c> and
/// resolved by <see cref="IBillingEventHandlerRegistry"/> on the event type. An
/// unclaimed type falls through to <see cref="NullBillingEventHandler"/>.</para>
///
/// <para><b>Idempotency contract</b>: <see cref="HandleAsync"/> must be safe to
/// re-run — re-dispatching an already-projected event is a no-op (the processor
/// short-circuits a <c>projected</c> row on replay; handlers stay side-effect-light).</para>
/// </summary>
public interface IBillingEventHandler
{
    /// <summary>Stripe event types this handler claims (e.g. <c>invoice.paid</c>).</summary>
    IReadOnlyCollection<string> HandledEventTypes { get; }

    /// <summary>
    /// Project the event (mirror write in sibling handlers) + emit the
    /// <c>BILLING.*</c> DCB event. Returns a <see cref="BillingFollowup"/> when
    /// heavy async work is needed (enqueued as a <c>PlatformQueuedTask</c> by the
    /// processor), or <c>null</c> for none.
    /// </summary>
    Task<BillingFollowup?> HandleAsync(BillingWebhookContext ctx, CancellationToken ct);
}
