namespace Tamma.Api.Services.EmailMediation;

// ============================================================
// Story 38 (Phase 1) — server-side binding record for the email-mediation
// endpoint. Bound from the engine client's camelCase JSON. Email is not
// repo-scoped — the acting tenant scopes the message + its audit events. NO
// credential travels here: the SMTP/Resend credential lives in Tamma.Api config,
// resolved inside the outbox-backed IEmailService; the engine holds nothing.
// ============================================================

/// <summary>
/// <c>POST /api/v1/notifications/email</c>. The composed body is already rendered
/// engine-side; the API accepts it into the credentialed, outbox-backed
/// <c>IEmailService</c> for delivery.
/// </summary>
public sealed record SendEmailRequest
{
    public string To { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
}
