namespace Tamma.Data.Entities;

/// <summary>
/// Story 35-5 — control-plane dedup + audit row for one inbound Stripe webhook
/// delivery. Stripe delivers at-least-once; the <c>UNIQUE</c> index on
/// <see cref="StripeEventId"/> makes reprocessing safe — a duplicate insert
/// collision is treated as an idempotent ack. Mirrors the
/// <see cref="PlatformWebhookDelivery"/> / <see cref="GitHubWebhookDelivery"/>
/// idempotency-journal pattern.
///
/// <para>CP-resident (control plane): billing is a cross-cutting platform
/// concern and the webhook arrives with no tenant context — the tenant is
/// resolved from <see cref="BillingCustomer.StripeCustomerId"/> and stamped on
/// <see cref="TenantId"/>. SaaS only — single-user never registers the webhook
/// route (35-1 <c>NullBillingProvider</c>).</para>
/// </summary>
public class BillingWebhookEvent
{
    /// <summary>Stable id (server default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Stripe event id (<c>evt_...</c>). UNIQUE — the dedup key.</summary>
    public string StripeEventId { get; set; } = null!;

    /// <summary>Stripe event type, e.g. <c>invoice.paid</c>.</summary>
    public string EventType { get; set; } = null!;

    /// <summary>
    /// Owning tenant, resolved from the event's Stripe customer id via
    /// <see cref="BillingCustomer"/>. Nullable — an event whose customer maps to
    /// no <see cref="BillingCustomer"/> is recorded <see cref="Status"/> =
    /// <c>skipped</c> with a null tenant (AC6).
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// The primary Stripe object id carried by the event
    /// (<c>sub_...</c>/<c>in_...</c>/<c>pi_...</c>/<c>dp_...</c>), extracted from
    /// <c>data.object.id</c>. Stamped on the DCB event's <c>stripeObjectId</c> tag.
    /// </summary>
    public string? StripeObjectId { get; set; }

    /// <summary>
    /// Processing status:
    /// <c>received | processing | projected | enqueued | failed | skipped</c>.
    /// </summary>
    public string Status { get; set; } = "received";

    /// <summary>Processing-attempt counter (incremented on each admin replay).</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Raw Stripe event JSON as delivered (already verified). Kept for admin
    /// replay/inspect. Stripe's own redaction applies; we add nothing PII-sensitive.
    /// </summary>
    public string Payload { get; set; } = "{}";

    /// <summary>Last handler error, scrubbed via <c>CredentialRedactor.Clean</c>.</summary>
    public string? LastError { get; set; }

    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
