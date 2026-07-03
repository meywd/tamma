namespace Tamma.Api.Services.Integrations;

/// <summary>
/// A resolved email transport credential bundle. The transport secret
/// (<see cref="ResendApiKey"/> / <see cref="SmtpPassword"/>) is request-scoped and
/// is NEVER logged, emitted onto a DCB event, or echoed on an HTTP response. The
/// non-secret <see cref="From"/> is the tenant-authorized sender identity threaded
/// onto the outgoing message.
/// </summary>
/// <param name="Transport">Transport kind: <c>smtp</c> or <c>resend</c>.</param>
/// <param name="From">The sender identity (RFC-5322 from address).</param>
/// <param name="ResendApiKey">Resend API key (required when
/// <see cref="Transport"/> is <c>resend</c>). Secret.</param>
/// <param name="SmtpHost">SMTP host (required when <see cref="Transport"/> is
/// <c>smtp</c>).</param>
/// <param name="SmtpPort">SMTP port (optional; transport default when null).</param>
/// <param name="SmtpUsername">SMTP username (optional).</param>
/// <param name="SmtpPassword">SMTP password (optional). Secret.</param>
/// <param name="SmtpUseStartTls">Whether to negotiate STARTTLS (optional).</param>
public sealed record EmailCredential(
    string Transport,
    string From,
    string? ResendApiKey = null,
    string? SmtpHost = null,
    int? SmtpPort = null,
    string? SmtpUsername = null,
    string? SmtpPassword = null,
    bool? SmtpUseStartTls = null)
{
    public const string TransportSmtp = "smtp";
    public const string TransportResend = "resend";
}

/// <summary>
/// A resolved <see cref="EmailCredential"/> plus the tier that answered
/// (tenant BYOK vs single-user system config).
/// </summary>
public sealed record EmailCredentialResolution(
    EmailCredential Credential, IntegrationCredentialSource Source);
