namespace Tamma.Api.Services.EmailMediation;

/// <summary>
/// Story 38 (Phase 1) — the managed email execution layer behind
/// <c>POST /api/v1/notifications/email</c>. Accepts the engine's rendered message
/// into the credentialed, outbox-backed <c>IEmailService</c> under the caller's
/// tenant context. The SMTP/Resend credential lives in Tamma.Api config; the engine
/// holds nothing. ALWAYS returns a typed, key-free <see cref="EmailMediationResult"/>
/// — a failure never throws a raw 5xx. Email is not repo-scoped, so there is no
/// cross-tenant repo guard.
/// </summary>
public interface IEmailMediationService
{
    Task<EmailMediationResult> SendEmailAsync(Guid? tenantId, SendEmailRequest body, CancellationToken ct = default);
}
