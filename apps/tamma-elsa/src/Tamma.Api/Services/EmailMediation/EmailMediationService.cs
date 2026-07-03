using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.Integrations;
using Tamma.Core.Logging;

namespace Tamma.Api.Services.EmailMediation;

/// <summary>
/// Story 38 (Phase 1) + integration BYOK — composes the email-mediation sequence
/// entirely inside <c>Tamma.Api</c>: resolve the acting tenant's email transport
/// credential per-request (BYOK→system→fail-loud, like git/LLM), then deliver via
/// the resolved credential's OWN transport authority. The transport secret NEVER
/// reaches the engine — it stays in Tamma.Api (cabinet or single-user config).
///
/// <para><b>Fail-loud tenant resolution (replaces the old SaaS-deny guard).</b>
/// The credential is resolved via <see cref="IEmailCredentialResolver"/>:
/// <list type="bullet">
///   <item><b>present</b> — the tenant's BYOK bundle (SaaS) or the single-user
///     <c>Email:*</c> config (system tier) ⇒ ALLOW, sending FROM the resolved
///     tenant-authorized identity, over that tier's own transport.</item>
///   <item><b>absent</b> — no per-tenant credential and no legitimate system tier
///     ⇒ <b>fail loud</b> with the typed
///     <see cref="EmailMediationFailureCodes.CredentialUnavailable"/> and a WARN
///     log; no transport is ever reached. This closes the confused-deputy: a
///     SaaS tenant can no longer send under a shared platform sender identity.</item>
/// </list></para>
///
/// <para><b>Per-tier transport authority (the anti-spoofing invariant).</b> The
/// transport is chosen by the tier that answered — NOT a single shared singleton:
/// <list type="bullet">
///   <item><b>SaaS BYOK (<see cref="IntegrationCredentialSource.Tenant"/>)</b> —
///     delivered via <see cref="ITenantEmailTransport"/> built from the tenant's
///     OWN bundle (their Resend key / their SMTP relay). The tenant's <c>From</c> is
///     therefore backed by the tenant's own sending authority (their DKIM); they can
///     only ever send as themselves. The platform singleton
///     <see cref="IEmailService"/> is <b>never</b> used for a SaaS tenant — using it
///     with a tenant-supplied <c>From</c> would be an open-relay / From-spoofing
///     hole (the platform's DKIM-signed transport emitting brand-impersonating
///     mail).</item>
///   <item><b>single-user system tier (<see cref="IntegrationCredentialSource.System"/>)</b>
///     — delivered via the platform singleton <see cref="IEmailService"/>, whose
///     <c>Email:*</c> process config IS the sole principal's own authority.</item>
/// </list></para>
///
/// <para>The DCB audit is owned by the transport itself
/// (<c>EMAIL.QUEUED/SENT/FAILED</c>); the mediation layer does NOT emit a duplicate
/// terminal event.</para>
/// </summary>
public sealed class EmailMediationService : IEmailMediationService
{
    private readonly IEmailService _email;
    private readonly ITenantEmailTransport _tenantTransport;
    private readonly IEmailCredentialResolver _credentials;
    private readonly ILogger<EmailMediationService> _logger;

    public EmailMediationService(
        IEmailService email,
        ITenantEmailTransport tenantTransport,
        IEmailCredentialResolver credentials,
        ILogger<EmailMediationService> logger)
    {
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _tenantTransport = tenantTransport ?? throw new ArgumentNullException(nameof(tenantTransport));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<EmailMediationResult> SendEmailAsync(Guid? tenantId, SendEmailRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // ── fail-loud per-tenant credential gate (runs BEFORE the transport) ──
        // SaaS: the tenant must have registered its own email credential; absent ⇒
        // fail loud rather than sending under a shared platform sender identity
        // (the confused-deputy). Single-user resolves the Email:* config tier.
        EmailCredentialResolution? resolution;
        try
        {
            resolution = await _credentials.ResolveAsync(tenantId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "email-mediation credential resolution threw; failing loud (CREDENTIAL_UNAVAILABLE). correlationId={CorrelationId}, tenantId={TenantId}",
                LogSanitizer.Clean(body.CorrelationId), tenantId);
            resolution = null;
        }

        if (resolution is null)
        {
            _logger.LogWarning(
                "email-mediation FAILED-LOUD (no email credential for tenant): send refused — register a per-tenant email credential or configure Email:* (single-user). correlationId={CorrelationId}, tenantId={TenantId}",
                LogSanitizer.Clean(body.CorrelationId), tenantId);

            return new EmailMediationResult
            {
                Success = false,
                Outcome = "Error",
                FailureCode = EmailMediationFailureCodes.CredentialUnavailable,
                FailureReason = "no email credential is configured for this tenant; register one via POST /api/v1/integrations/email/credential.",
                CorrelationId = body.CorrelationId,
            };
        }

        if (string.IsNullOrWhiteSpace(body.To))
        {
            return new EmailMediationResult
            {
                Success = false,
                Outcome = "Error",
                FailureCode = EmailMediationFailureCodes.PlatformError,
                FailureReason = "recipient (to) is required",
                CorrelationId = body.CorrelationId,
            };
        }

        try
        {
            // The engine sends a single already-rendered body; use it for BOTH the
            // plain-text (required) and HTML variants. From = the resolved
            // tenant-authorized sender identity. The tenant scopes the message +
            // its EMAIL.* audit events.
            var message = new EmailMessage(
                To: body.To,
                Subject: body.Subject,
                Html: body.Body,
                Text: body.Body,
                From: resolution.Credential.From,
                Template: null,
                TenantId: tenantId,
                UserId: null);

            // Route by the tier that answered — the anti-spoofing invariant. A SaaS
            // BYOK credential is delivered via the TENANT'S OWN transport (their
            // Resend key / SMTP relay), never the platform singleton with a
            // tenant-supplied From. Single-user's system tier uses the platform
            // singleton, whose Email:* config IS the sole principal's authority.
            var txnId = resolution.Source == IntegrationCredentialSource.Tenant
                ? await _tenantTransport.SendAsync(resolution.Credential, message, ct).ConfigureAwait(false)
                : await _email.SendAsync(message, ct).ConfigureAwait(false);

            return new EmailMediationResult
            {
                Success = true,
                Outcome = "Queued",
                TxnId = txnId,
                CorrelationId = body.CorrelationId,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail-soft: a missing notification must not break the workflow — surface a
            // typed PLATFORM_ERROR inside 200 success:false (never a raw 5xx). The
            // recipient/body are NOT logged.
            _logger.LogError(ex,
                "email-mediation send threw; returning typed PLATFORM_ERROR (never a raw 5xx). correlationId={CorrelationId}, tenantId={TenantId}",
                LogSanitizer.Clean(body.CorrelationId), tenantId);

            return new EmailMediationResult
            {
                Success = false,
                Outcome = "Error",
                FailureCode = EmailMediationFailureCodes.PlatformError,
                FailureReason = "an unexpected error occurred processing the email operation",
                CorrelationId = body.CorrelationId,
            };
        }
    }
}
