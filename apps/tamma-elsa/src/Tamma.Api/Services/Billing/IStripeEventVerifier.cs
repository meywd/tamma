using Stripe;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 — thin seam over <see cref="Stripe.EventUtility"/> so the endpoint
/// is testable without re-implementing Stripe's <c>t=</c>/<c>v1=</c> HMAC scheme,
/// and so a test can compute a real signature against a test <c>whsec_</c>. The
/// default implementation delegates to the SDK — we NEVER hand-roll the HMAC
/// (story Dev Note 4).
/// </summary>
public interface IStripeEventVerifier
{
    /// <summary>
    /// Verify + parse a signed delivery. Throws <see cref="StripeException"/>
    /// when the signature is invalid, missing, or outside the tolerance window.
    /// </summary>
    Event Construct(string rawBody, string signatureHeader, string signingSecret);

    /// <summary>
    /// Parse a stored (already-verified) payload back into a
    /// <see cref="Stripe.Event"/> for admin replay — no signature check.
    /// </summary>
    Event Parse(string rawBody);
}

/// <inheritdoc />
public sealed class StripeEventVerifier : IStripeEventVerifier
{
    public Event Construct(string rawBody, string signatureHeader, string signingSecret) =>
        // throwOnApiVersionMismatch:false — a Stripe account whose default API
        // version differs from the SDK's pinned version must NOT be rejected as a
        // bad signature (that would 400 valid events); we only care that the HMAC
        // verifies. tolerance:300 = Stripe's default replay window.
        EventUtility.ConstructEvent(
            rawBody, signatureHeader, signingSecret,
            tolerance: 300, throwOnApiVersionMismatch: false);

    public Event Parse(string rawBody) =>
        EventUtility.ParseEvent(rawBody, throwOnApiVersionMismatch: false);
}
