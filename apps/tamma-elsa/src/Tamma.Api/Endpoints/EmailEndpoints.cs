using Microsoft.AspNetCore.Http;
using Tamma.Api.Services.EmailMediation;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 38 (Phase 1) — the internal, engine-only email-mediation endpoint
/// (<c>POST /api/v1/notifications/email</c>). Same engine-only plane as
/// <c>/api/v1/llm/call</c> / <c>/api/v1/notifications/slack</c>:
/// <c>EngineServiceOnly</c> auth (missing/invalid bearer ⇒ 401; user JWT ⇒ 403), the
/// acting tenant is the auth-derived <see cref="ITenantContext"/> (X-Tenant-Id, NEVER
/// the body). Email is not repo-scoped, so there is no tenant↔repo guard. Delegates
/// to <see cref="IEmailMediationService"/>, which accepts the message into the
/// credentialed, outbox-backed <c>IEmailService</c>; the SMTP/Resend credential
/// never reaches the engine.
/// </summary>
public static class EmailEndpoints
{
    public static async Task<IResult> SendEmail(
        SendEmailRequest body,
        ITenantContext tenantContext, IEmailMediationService email, CancellationToken ct)
    {
        var result = await email.SendEmailAsync(tenantContext.TenantId, body, ct).ConfigureAwait(false);
        return result.ToHttpResult();
    }
}
