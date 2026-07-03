using Tamma.Api.Services.Email;
using Tamma.Api.Services.Integrations;

namespace Tamma.Api.Services.EmailMediation;

/// <summary>
/// SaaS BYOK email transport — sends a single message via the TENANT'S OWN sending
/// authority resolved from the per-tenant credential bundle: their Resend API key,
/// or their SMTP relay (host/port/username/password). This is the anti-spoofing
/// control at the heart of email BYOK.
///
/// <para><b>Why not the platform singleton <see cref="IEmailService"/>?</b> The
/// platform transport is DKIM-signed for the platform's own domain. If a SaaS
/// tenant's message were delivered through it with only a tenant-supplied
/// <c>From</c>, a <c>tenant_admin</c> could set
/// <c>From: security@&lt;platform-domain&gt;</c> and the platform would emit a
/// valid, brand-impersonating email — the tenant's stored transport secret never
/// used, the BYOK gate illusory. Routing SaaS sends through THIS seam means the
/// <c>From</c> is always backed by the tenant's own transport authority (their DKIM
/// / their relay), so a tenant can only ever send as themselves. The platform
/// singleton is reserved for the single-user system tier, whose <c>Email:*</c>
/// config IS the sole principal's authority.</para>
///
/// <para>Emits the same <c>EMAIL.QUEUED / EMAIL.SENT.SUCCESS / EMAIL.SENT.FAILED</c>
/// DCB audit the platform transport does, so a SaaS tenant's mail keeps a full
/// audit trail. Never throws for transport failures — those surface via
/// <c>EMAIL.SENT.FAILED</c> and the returned txn id (mirrors the
/// <see cref="IEmailService"/> contract).</para>
/// </summary>
public interface ITenantEmailTransport
{
    /// <summary>
    /// Send <paramref name="message"/> via the transport described by
    /// <paramref name="credential"/> (<c>resend</c> or <c>smtp</c>). Returns the
    /// transaction id correlating the emitted EMAIL.* events.
    /// </summary>
    Task<Guid> SendAsync(EmailCredential credential, EmailMessage message, CancellationToken ct = default);
}
