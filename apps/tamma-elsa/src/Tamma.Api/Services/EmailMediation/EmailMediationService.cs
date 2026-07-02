using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.PromptStore;
using Tamma.Core.Logging;

namespace Tamma.Api.Services.EmailMediation;

/// <summary>
/// Story 38 (Phase 1) — composes the email-mediation sequence entirely inside
/// <c>Tamma.Api</c>: accept the engine's rendered message into the credentialed,
/// outbox-backed <see cref="IEmailService"/> (SMTP / Resend / in-memory, chosen by
/// <c>AddEmailServices</c> config) under the caller's tenant context. Unlike git/CI
/// there is no per-tenant BYOK token resolver and no repo guard — email is a single,
/// server-side, config-provided integration (mirrors the Slack outbox plane).
///
/// <para>The DCB audit is owned by <see cref="IEmailService"/> itself, which emits
/// <see cref="EmailEventTypes.Queued"/> on accept and later
/// <see cref="EmailEventTypes.Sent"/> / <see cref="EmailEventTypes.Failed"/> from the
/// transport; the mediation layer therefore does NOT emit a duplicate terminal event
/// (that would double-audit and collide with the subsystem's <c>EMAIL.SENT.*</c>
/// types). The email credential NEVER reaches the engine — it stays in Tamma.Api
/// config.</para>
/// </summary>
public sealed class EmailMediationService : IEmailMediationService
{
    /// <summary>
    /// Config flag (default FALSE) that opts a SaaS deployment back into
    /// tenant-workflow-initiated mediated email sends. See the SaaS guard in
    /// <see cref="SendEmailAsync"/> and <see cref="EmailMediationFailureCodes.MediationDeniedInSaaS"/>.
    /// </summary>
    internal const string AllowMediatedSendInSaaSKey = "Email:AllowMediatedSendInSaaS";

    private readonly IEmailService _email;
    private readonly ITammaModeProvider _mode;
    private readonly IConfiguration _config;
    private readonly ILogger<EmailMediationService> _logger;

    public EmailMediationService(
        IEmailService email,
        ITammaModeProvider mode,
        IConfiguration config,
        ILogger<EmailMediationService> logger)
    {
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<EmailMediationResult> SendEmailAsync(Guid? tenantId, SendEmailRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        // SaaS fail-closed tenant guard (mirrors the JIRA/git mediation guards).
        // A mediated send composes mail FROM the platform's configured sender identity
        // (Email:From) — there is no per-tenant From/domain allowlist here. In SaaS a
        // tenant workflow could therefore emit arbitrary mail under the platform's
        // reputation/domain (spam/phishing amplification), so deny-by-default. An
        // operator opts back in with Email:AllowMediatedSendInSaaS=true once a
        // per-tenant sender policy exists. Single-user mode is always allowed (the sole
        // principal owns the domain). Fail-soft: return a typed denial inside 200
        // success:false, never a raw 5xx. Only TENANT-WORKFLOW-initiated sends reach
        // this service; system emails (welcome/verification/password-reset) call
        // IEmailService directly and are unaffected.
        if (_mode.Mode == TammaMode.SaaS
            && !_config.GetValue(AllowMediatedSendInSaaSKey, false))
        {
            _logger.LogWarning(
                "email-mediation send DENIED in SaaS mode: no per-tenant sender allowlist; " +
                "set {ConfigKey}=true to opt in. correlationId={CorrelationId}, tenantId={TenantId}",
                AllowMediatedSendInSaaSKey, LogSanitizer.Clean(body.CorrelationId), tenantId);

            return new EmailMediationResult
            {
                Success = false,
                Outcome = "Denied",
                FailureCode = EmailMediationFailureCodes.MediationDeniedInSaaS,
                FailureReason = "mediated email send is not permitted in SaaS mode",
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
            // plain-text (required) and HTML variants. The tenant scopes the message
            // + its EMAIL.* audit events; no per-user identity at the engine-service
            // principal plane.
            var message = new EmailMessage(
                To: body.To,
                Subject: body.Subject,
                Html: body.Body,
                Text: body.Body,
                From: null,
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
