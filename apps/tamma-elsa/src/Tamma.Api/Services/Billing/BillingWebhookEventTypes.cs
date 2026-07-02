namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 — canonical constants for the Stripe event types 35-5's default
/// handlers claim, and the <c>BILLING.*</c> DCB event types they emit
/// (<c>AGGREGATE.ACTION.STATUS</c> convention). Kept as constants so the
/// handlers, the registry, and the tests cannot drift.
/// </summary>
public static class BillingWebhookEventTypes
{
    // ── Inbound Stripe event types ──
    public const string SubscriptionCreated = "customer.subscription.created";
    public const string SubscriptionUpdated = "customer.subscription.updated";
    public const string SubscriptionDeleted = "customer.subscription.deleted";
    public const string SubscriptionTrialWillEnd = "customer.subscription.trial_will_end";

    public const string InvoiceCreated = "invoice.created";
    public const string InvoiceFinalized = "invoice.finalized";
    public const string InvoicePaid = "invoice.paid";
    public const string InvoicePaymentFailed = "invoice.payment_failed";

    public const string PaymentIntentSucceeded = "payment_intent.succeeded";
    public const string PaymentIntentPaymentFailed = "payment_intent.payment_failed";

    public const string ChargeDisputeCreated = "charge.dispute.created";

    // ── Emitted BILLING.* DCB event types ──
    public const string DcbSubscriptionCreated = "BILLING.SUBSCRIPTION.CREATED";
    public const string DcbSubscriptionUpdated = "BILLING.SUBSCRIPTION.UPDATED";
    public const string DcbSubscriptionDeleted = "BILLING.SUBSCRIPTION.DELETED";
    public const string DcbSubscriptionTrialEnding = "BILLING.SUBSCRIPTION.TRIAL_ENDING";

    public const string DcbInvoiceCreated = "BILLING.INVOICE.CREATED";
    public const string DcbInvoiceFinalized = "BILLING.INVOICE.FINALIZED";
    public const string DcbInvoicePaid = "BILLING.INVOICE.PAID";
    public const string DcbInvoicePaymentFailed = "BILLING.INVOICE.PAYMENT_FAILED";

    public const string DcbPaymentSucceeded = "BILLING.PAYMENT.SUCCEEDED";
    public const string DcbPaymentFailed = "BILLING.PAYMENT.FAILED";

    public const string DcbDisputeOpened = "BILLING.DISPUTE.OPENED";

    // ── Operational (system) DCB event types ──
    public const string DcbWebhookSkipped = "BILLING.WEBHOOK.SKIPPED";
    public const string DcbWebhookFailed = "BILLING.WEBHOOK.FAILED";

    /// <summary>The <c>PlatformQueuedTask.Type</c> for fast-ack follow-up work (AC10).</summary>
    public const string FollowupTaskType = "billing.webhook.followup";
}
