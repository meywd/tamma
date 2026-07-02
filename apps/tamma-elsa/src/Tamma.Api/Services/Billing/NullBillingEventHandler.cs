namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 (AC7, AC11) — the logging default handler. Used by the processor
/// for any Stripe event type NO registered <see cref="IBillingEventHandler"/>
/// claims. Emits no <c>BILLING.*</c> projection event and returns no follow-up;
/// the processor records the row <c>Status = skipped</c> and acks <c>200</c> so
/// an unknown/irrelevant event never triggers a Stripe retry storm.
///
/// <para>Excluded from <see cref="BillingEventHandlerRegistry"/> (it claims no
/// event types) — it is the explicit fallthrough, never a registered claimant.</para>
/// </summary>
public sealed class NullBillingEventHandler : IBillingEventHandler
{
    private readonly ILogger<NullBillingEventHandler> _logger;

    public NullBillingEventHandler(ILogger<NullBillingEventHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Claims nothing — the registry skips it; the processor uses it as fallthrough.</summary>
    public IReadOnlyCollection<string> HandledEventTypes => Array.Empty<string>();

    public Task<BillingFollowup?> HandleAsync(BillingWebhookContext ctx, CancellationToken ct)
    {
        _logger.LogInformation(
            "No billing handler for Stripe event type {EventType} "
            + "(stripeEventId={StripeEventId}, tenantId={TenantId}); recording skipped.",
            ctx.EventType, ctx.StripeEventId, ctx.TenantId);
        return Task.FromResult<BillingFollowup?>(null);
    }
}
