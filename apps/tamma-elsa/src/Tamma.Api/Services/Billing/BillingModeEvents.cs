namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-2 — DCB event-type constants for the billing-mode tag propagation
/// (<c>AGGREGATE.ACTION.STATUS</c> convention). The usage events carry the
/// <c>billing_mode</c> tag so Story 35-3's metering can split billable
/// (platform) from non-billable (byok) token usage off the event stream with no
/// join. <see cref="BillingModeMismatch"/> makes an owner-vs-runtime divergence
/// auditable (34-3 mode ≠ 32-3 credential source; 32-3 wins for the tag).
/// </summary>
public static class BillingModeEvents
{
    /// <summary>A successful proxied LLM call. Tags: tenantId, billing_mode, provider, model.</summary>
    public const string LlmCallSuccess = "LLM.CALL.SUCCESS";

    /// <summary>A failed / over-budget / upstream-error LLM call. Same billing_mode tag.</summary>
    public const string LlmCallFailed = "LLM.CALL.FAILED";

    /// <summary>
    /// The 34-3 declared mode disagreed with the 32-3 runtime credential source
    /// on a call. Tags: tenantId, provider, mode34, source32. 32-3 wins for the
    /// stamped tag (it is the credential actually used on the wire).
    /// </summary>
    public const string BillingModeMismatch = "BILLING.MODE.MISMATCH";
}
