using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.Integrations;
using Tamma.Core.Logging;

namespace Tamma.Api.Services.EmailMediation;

/// <summary>
/// Story 38 (Phase 1) + integration BYOK — composes the email-mediation sequence
/// entirely inside <c>Tamma.Api</c>: resolve the acting tenant's email transport
/// credential per-request (BYOK→system→fail-loud, like git/LLM), thread its
/// tenant-authorized <c>From</c> identity onto the rendered message, and accept it
/// into the credentialed, outbox-backed <see cref="IEmailService"/> for delivery.
/// The transport secret NEVER reaches the engine — it stays in Tamma.Api (cabinet
/// or single-user config).
///
/// <para><b>Fail-loud tenant resolution (replaces the old SaaS-deny guard).</b>
/// The credential is resolved via <see cref="IEmailCredentialResolver"/>:
/// <list type="bullet">
///   <item><b>present</b> — the tenant's BYOK bundle (SaaS) or the single-user
///     <c>Email:*</c> config (system tier) ⇒ ALLOW, sending FROM the resolved
///     tenant-authorized identity.</item>
///   <item><b>absent</b> — no per-tenant credential and no legitimate system tier
///     ⇒ <b>fail loud</b> with the typed
///     <see cref="EmailMediationFailureCodes.CredentialUnavailable"/> and a WARN
///     log; the transport is NEVER reached. This closes the confused-deputy: a
///     SaaS tenant can no longer send under a shared platform sender identity.</item>
/// </list></para>
///
/// <para>The DCB audit is owned by <see cref="IEmailService"/> itself
/// (<c>EMAIL.QUEUED/SENT/FAILED</c>); the mediation layer does NOT emit a duplicate
/// terminal event.</para>
///
/// <para><b>Bounded transport threading.</b> The resolved <c>From</c> is threaded
/// onto the message (the tenant-owned sender identity — the anti-spoofing control);
/// swapping the singleton outbox transport's own secret (the tenant's Resend key /
/// SMTP endpoint) per-tenant is a follow-on, because the outbox transport reads
/// process config and carrying a per-tenant SMTP endpoint would need an outbox
/// column (NON-migration forbids it). Single-user's transport IS the config the
/// resolver mirrors, so delivery is fully consistent there.</para>
/// </summary>
public sealed class EmailMediationService : IEmailMediationService
{
    private readonly IEmailService _email;
    private readonly IEmailCredentialResolver _credentials;
    private readonly ILogger<EmailMediationService> _logger;

    public EmailMediationService(
        IEmailService email,
        IEmailCredentialResolver credentials,
        ILogger<EmailMediationService> logger)
    {
        _email = email ?? throw new ArgumentNullException(nameof(email));
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

            var txnId = await _email.SendAsync(message, ct).ConfigureAwait(false);

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
