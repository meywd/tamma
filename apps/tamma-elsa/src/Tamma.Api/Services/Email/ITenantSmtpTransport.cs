using Tamma.Api.Services.Integrations;

namespace Tamma.Api.Services.Email;

/// <summary>
/// Per-request SMTP send bound to a TENANT-supplied transport (BYOK). Unlike
/// <see cref="ISmtpTransport"/> — which reads the process <c>Email:Smtp:*</c>
/// config and delivers the platform's outbox rows — this seam takes the tenant's
/// own SMTP host/port/credentials from the resolved
/// <see cref="EmailCredential"/> bundle and sends via THAT relay. This is the
/// anti-spoofing control: a SaaS tenant's <c>From</c> only ever rides the tenant's
/// own sending authority, never the platform relay.
///
/// <para>Production is <see cref="MailKitTenantSmtpTransport"/>; tests supply a
/// fake so the per-tenant routing can be asserted without a real relay. Throws for
/// ANY transport failure (connect / auth / send) — <see cref="EmailMediation.TenantEmailTransport"/>
/// catches, emits the <c>EMAIL.SENT.FAILED</c> audit, and never rethrows.</para>
/// </summary>
public interface ITenantSmtpTransport
{
    /// <summary>Deliver <paramref name="message"/> via the SMTP relay described by
    /// <paramref name="credential"/> (host/port/username/password), using
    /// <see cref="EmailCredential.From"/> as the envelope sender.</summary>
    Task SendAsync(EmailCredential credential, EmailMessage message, CancellationToken ct = default);
}
