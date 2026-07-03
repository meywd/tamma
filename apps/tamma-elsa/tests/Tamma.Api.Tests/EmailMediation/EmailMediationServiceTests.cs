using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.EmailMediation;
using Tamma.Api.Services.Integrations;

namespace Tamma.Api.Tests.EmailMediation;

/// <summary>
/// Integration BYOK — <see cref="EmailMediationService"/> composition, pinning the
/// <b>anti-spoofing transport-authority invariant</b>: a SaaS tenant's message is
/// delivered via the TENANT'S OWN transport (<see cref="ITenantEmailTransport"/>)
/// resolved from its bundle — the platform singleton <see cref="IEmailService"/> is
/// NEVER used for a SaaS tenant (that would let a tenant_admin send a
/// platform-DKIM-signed, brand-impersonating email with an arbitrary <c>From</c>).
/// The single-user system tier still uses the platform singleton, whose
/// <c>Email:*</c> config IS the sole principal's authority.
///
/// <para>Pins: SaaS tenant tier ⇒ tenant transport used with the tenant credential,
/// platform singleton NOT called; single-user system tier ⇒ platform singleton used,
/// tenant transport NOT called; ABSENT credential ⇒ fail-loud
/// <see cref="EmailMediationFailureCodes.CredentialUnavailable"/> with NEITHER
/// transport reached; a missing recipient / transport exception is fail-soft
/// PLATFORM_ERROR.</para>
/// </summary>
[TestFixture]
public class EmailMediationServiceTests
{
    private const string ResolvedFrom = "team@tenant.example.com";

    private Mock<IEmailService> _platform = null!;
    private Mock<ITenantEmailTransport> _tenantTransport = null!;
    private FakeResolver _resolver = null!;
    private EmailMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    private static readonly EmailCredential ResendBundle =
        new(EmailCredential.TransportResend, ResolvedFrom, ResendApiKey: "fake-resend-key");

    [SetUp]
    public void SetUp()
    {
        _platform = new Mock<IEmailService>(MockBehavior.Strict);
        _tenantTransport = new Mock<ITenantEmailTransport>(MockBehavior.Strict);
        _resolver = new FakeResolver
        {
            Resolution = new EmailCredentialResolution(ResendBundle, IntegrationCredentialSource.Tenant),
        };
        _sut = new EmailMediationService(
            _platform.Object, _tenantTransport.Object, _resolver, NullLogger<EmailMediationService>.Instance);
    }

    private static SendEmailRequest Body() => new()
    {
        To = "dev@example.com", Subject = "Build passed", Body = "All green", CorrelationId = "corr-e",
    };

    [Test]
    public async Task Send_SaasTenantByok_UsesTenantTransport_NeverPlatformSingleton()
    {
        var txn = Guid.NewGuid();
        EmailCredential? capturedCred = null;
        EmailMessage? capturedMsg = null;
        _tenantTransport
            .Setup(t => t.SendAsync(It.IsAny<EmailCredential>(), It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailCredential, EmailMessage, CancellationToken>((c, m, _) => { capturedCred = c; capturedMsg = m; })
            .ReturnsAsync(txn);

        var result = await _sut.SendEmailAsync(_tenant, Body());

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Queued");
        result.TxnId.Should().Be(txn);

        // Delivered via the tenant's OWN transport, with the tenant's bundle …
        capturedCred.Should().BeSameAs(ResendBundle, "the SaaS send must ride the tenant's own transport authority");
        capturedMsg!.From.Should().Be(ResolvedFrom);
        capturedMsg.TenantId.Should().Be(_tenant);
        capturedMsg.To.Should().Be("dev@example.com");
        capturedMsg.Text.Should().Be("All green");

        // … and NEVER the platform DKIM-signed singleton (the From-spoofing hole).
        _platform.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never,
            "a SaaS tenant must never be delivered via the platform transport with a tenant-supplied From");
    }

    [Test]
    public async Task Send_SingleUserSystemTier_UsesPlatformSingleton_NeverTenantTransport()
    {
        _resolver.Resolution = new EmailCredentialResolution(
            new EmailCredential(EmailCredential.TransportSmtp, "ops@self-hosted.example", SmtpHost: "smtp.self.example"),
            IntegrationCredentialSource.System);

        var txn = Guid.NewGuid();
        EmailMessage? captured = null;
        _platform.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync(txn);

        var result = await _sut.SendEmailAsync(_tenant, Body());

        result.Success.Should().BeTrue();
        result.TxnId.Should().Be(txn);
        captured!.From.Should().Be("ops@self-hosted.example");

        _tenantTransport.Verify(
            t => t.SendAsync(It.IsAny<EmailCredential>(), It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()),
            Times.Never, "the single-user system tier uses the platform/config transport, not a per-tenant transport");
    }

    [Test]
    public async Task Send_NoCredential_FailsLoud_NeverCallsAnyTransport()
    {
        _resolver.Resolution = null;

        var result = await _sut.SendEmailAsync(_tenant, Body());

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be("Error");
        result.FailureCode.Should().Be(EmailMediationFailureCodes.CredentialUnavailable);
        result.CorrelationId.Should().Be("corr-e");
        _platform.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _tenantTransport.Verify(
            t => t.SendAsync(It.IsAny<EmailCredential>(), It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()),
            Times.Never, "a fail-loud send must never reach any transport");
    }

    [Test]
    public async Task Send_MissingRecipient_TypedPlatformError_NeverCallsAnyTransport()
    {
        var result = await _sut.SendEmailAsync(_tenant, new SendEmailRequest { To = "", Subject = "s", Body = "b", CorrelationId = "c" });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(EmailMediationFailureCodes.PlatformError);
        _platform.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _tenantTransport.Verify(
            t => t.SendAsync(It.IsAny<EmailCredential>(), It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Send_TenantTransportThrows_FailSoft_TypedPlatformError_No5xx()
    {
        _tenantTransport
            .Setup(t => t.SendAsync(It.IsAny<EmailCredential>(), It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("tenant transport failed"));

        var result = await _sut.SendEmailAsync(_tenant, Body());

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be("Error");
        result.FailureCode.Should().Be(EmailMediationFailureCodes.PlatformError);
        result.CorrelationId.Should().Be("corr-e");
    }

    private sealed class FakeResolver : IEmailCredentialResolver
    {
        public EmailCredentialResolution? Resolution { get; set; }
        public Task<EmailCredentialResolution?> ResolveAsync(Guid? tenantId, CancellationToken ct = default)
            => Task.FromResult(Resolution);
        public void Invalidate(Guid? tenantId) { }
    }
}
