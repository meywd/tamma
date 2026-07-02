namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 — the per-event context handed to an <see cref="IBillingEventHandler"/>.
/// The processor has already verified the signature, deduped the delivery, and
/// resolved the tenant; the handler only projects + emits its <c>BILLING.*</c>
/// DCB event.
///
/// <para>The primary Stripe object id (<see cref="StripeObjectId"/>) and the
/// customer id are extracted from the raw payload's <c>data.object</c> by the
/// processor — deterministic and Stripe-SDK-shape-independent — so handlers
/// never re-parse the body. The typed <see cref="StripeEvent"/> is still
/// supplied for handlers that want the strongly-typed object.</para>
/// </summary>
public sealed record BillingWebhookContext(
    Stripe.Event StripeEvent,
    Guid TenantId,
    string EventType,
    string StripeEventId,
    string? StripeObjectId,
    string RawPayload);

/// <summary>
/// A follow-up work item a handler wants run asynchronously (dunning
/// escalation, email, Stripe round-trips) rather than inline — enqueued by the
/// processor as a <c>billing.webhook.followup</c> <c>PlatformQueuedTask</c>
/// (AC10). <see cref="Payload"/> is the JSON payload for that task.
/// </summary>
public sealed record BillingFollowup(string Subtype, string Payload);
