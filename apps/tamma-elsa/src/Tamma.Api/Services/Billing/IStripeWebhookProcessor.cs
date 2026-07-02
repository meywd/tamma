namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 — the dedupe + tenant-resolve + dispatch + DCB-emit + fast-ack
/// core of the webhook pipeline. The endpoint verifies the Stripe signature and
/// hands the parsed <see cref="Stripe.Event"/> + raw body here; the processor
/// owns everything after verification. Split from the endpoint so it is unit
/// testable with an in-memory CP context and mocked repositories.
/// </summary>
public interface IStripeWebhookProcessor
{
    /// <summary>
    /// Process a freshly-verified webhook delivery. Inserts the dedup row,
    /// resolves the tenant, dispatches the handler, emits the <c>BILLING.*</c>
    /// DCB event, and enqueues any follow-up. Never throws for a projection
    /// failure (records <c>failed</c> + acks) — only a CP dedup-row WRITE failure
    /// bubbles (so the endpoint returns 503 and Stripe retries the one case where
    /// retry is wanted).
    /// </summary>
    Task<WebhookProcessResult> ProcessAsync(
        Stripe.Event stripeEvent, string rawPayload, CancellationToken ct = default);

    /// <summary>
    /// Admin replay (AC12) — re-dispatch a stored <c>BillingWebhookEvent</c> by
    /// id. Re-running an already-<c>projected</c>/<c>enqueued</c> row is a no-op
    /// (idempotent). Returns <c>null</c> when the row does not exist.
    /// </summary>
    Task<WebhookProcessResult?> ReplayAsync(Guid webhookEventId, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a webhook process/replay. <see cref="Status"/> mirrors the terminal
/// <c>BillingWebhookEvent.Status</c> (or <c>duplicate</c> for a deduped
/// redelivery). The endpoint echoes it in the <c>200</c> ack body.
/// </summary>
public sealed record WebhookProcessResult(string Status)
{
    public const string DuplicateStatus = "duplicate";
    public const string SkippedStatus = "skipped";
    public const string ProjectedStatus = "projected";
    public const string EnqueuedStatus = "enqueued";
    public const string FailedStatus = "failed";

    public static WebhookProcessResult Duplicate { get; } = new(DuplicateStatus);
    public static WebhookProcessResult Skipped { get; } = new(SkippedStatus);
    public static WebhookProcessResult Projected { get; } = new(ProjectedStatus);
    public static WebhookProcessResult Enqueued { get; } = new(EnqueuedStatus);
    public static WebhookProcessResult Failed { get; } = new(FailedStatus);
}
