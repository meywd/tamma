using System.Text;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services.Billing;

namespace Tamma.Api.Endpoints.Billing;

/// <summary>
/// Story 35-5 — <c>POST /api/v1/billing/stripe/webhook</c>. Anonymous at the
/// app-auth layer (Stripe calls it), signature-gated instead. Mirrors
/// <c>GitHubEndpoints.Webhooks</c>: <c>EnableBuffering()</c> + a leave-open
/// <see cref="StreamReader"/> to capture the RAW body (any body re-serialization
/// would break signature verification), then verify with the Stripe SDK before
/// any processing.
///
/// <para>Status discipline (AC1–AC3, AC11): missing signature → <c>400</c>;
/// unresolvable secret → <c>503</c> (never fail open); invalid signature →
/// <c>400</c> (WARN, never the raw body); everything else acks <c>200</c> — so a
/// permanently-unprojectable event never triggers a Stripe retry storm.</para>
/// </summary>
public static class StripeWebhookEndpoint
{
    public static async Task<IResult> Receive(
        HttpContext context,
        [FromServices] IStripeSigningSecretSource secretSource,
        [FromServices] IStripeEventVerifier verifier,
        [FromServices] IStripeWebhookProcessor processor,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("StripeWebhookEndpoint");

        var signature = context.Request.Headers["Stripe-Signature"].FirstOrDefault();
        if (string.IsNullOrEmpty(signature))
        {
            logger.LogWarning("Stripe webhook rejected: missing Stripe-Signature header.");
            return Results.BadRequest(new { error = "missing signature" });
        }

        // Capture the raw body without consuming it for any later middleware.
        context.Request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(
            context.Request.Body, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(context.RequestAborted);
            context.Request.Body.Position = 0;
        }

        // Secret from the Epic 29 cabinet — never IConfiguration. Fail CLOSED.
        var signingSecret = await secretSource
            .GetSigningSecretAsync(context.RequestAborted);
        if (string.IsNullOrEmpty(signingSecret))
        {
            logger.LogError(
                "Stripe webhook secret unresolvable from the cabinet; returning 503 "
                + "(fail closed — never fail open on a missing secret).");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        Stripe.Event stripeEvent;
        try
        {
            stripeEvent = verifier.Construct(rawBody, signature, signingSecret);
        }
        catch (Stripe.StripeException ex)
        {
            // Never log the raw body or the signature header.
            logger.LogWarning(
                "Stripe webhook signature rejected: {Reason}", ex.StripeError?.Message ?? ex.Message);
            return Results.BadRequest(new { error = "invalid signature" });
        }

        var result = await processor
            .ProcessAsync(stripeEvent, rawBody, context.RequestAborted);
        return Results.Ok(new { received = true, status = result.Status });
    }
}
