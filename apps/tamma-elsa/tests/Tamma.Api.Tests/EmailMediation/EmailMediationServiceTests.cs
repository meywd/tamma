using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.EmailMediation;
using Tamma.Api.Services.Integrations;

namespace Tamma.Api.Tests.EmailMediation;

/// <summary>
/// Integration BYOK — <see cref="EmailMediationService"/> composition. The old
/// SaaS-deny guard is replaced by per-tenant credential resolution: the tenant's
/// email credential is resolved (BYOK→system→fail-loud), its tenant-authorized
/// <c>From</c> is threaded onto the message, and the message is accepted into the
/// outbox-backed <see cref="IEmailService"/>.
///
/// <para>Pins: present credential ⇒ Queued with the resolved From on the message;
/// ABSENT credential ⇒ fail-loud
/// <see cref="EmailMediationFailureCodes.CredentialUnavailable"/> with the transport
/// NEVER reached; a missing recipient / accept exception is fail-soft PLATFORM_ERROR.</para>
/// </summary>
[TestFixture]
public class EmailMediationServiceTests
{
    private const string ResolvedFrom = "team@tenant.example.com";

    private Mock<IEmailService> _email = null!;
    private FakeResolver _resolver = null!;
    private EmailMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _email = new Mock<IEmailService>(MockBehavior.Strict);
        _resolver = new FakeResolver
        {
            Resolution = new EmailCredentialResolution(
                new EmailCredential(EmailCredential.TransportResend, ResolvedFrom, ResendApiKey: "fake-resend-key"),
                IntegrationCredentialSource.Tenant),
        };
        _sut = new EmailMediationService(_email.Object, _resolver, NullLogger<EmailMediationService>.Instance);
    }

    private static SendEmailRequest Body() => new()
    {
        To = "dev@example.com", Subject = "Build passed", Body = "All green", CorrelationId = "corr-e",
    };

    [Test]
    public async Task Send_CredentialResolved_ThreadsFrom_AcceptsIntoOutbox_ReturnsTxn()
    {
        var txn = Guid.NewGuid();
        EmailMessage? captured = null;
        _email.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync(txn);

        var result = await _sut.SendEmailAsync(_tenant, Body());

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be("Queued");
        result.TxnId.Should().Be(txn);
        captured.Should().NotBeNull();
        captured!.From.Should().Be(ResolvedFrom, "the resolved tenant-authorized sender identity is threaded onto the message");
        captured.TenantId.Should().Be(_tenant);
        captured.To.Should().Be("dev@example.com");
        captured.Text.Should().Be("All green");
    }

    [Test]
    public async Task Send_NoCredential_FailsLoud_NeverCallsTransport()
    {
        _resolver.Resolution = null;

        var result = await _sut.SendEmailAsync(_tenant, Body());

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be("Error");
        result.FailureCode.Should().Be(EmailMediationFailureCodes.CredentialUnavailable);
        result.CorrelationId.Should().Be("corr-e");
        _email.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never,
            "a fail-loud send must never reach the outbox/transport");
    }

    [Test]
    public async Task Send_MissingRecipient_TypedPlatformError_NeverCallsTransport()
    {
        var result = await _sut.SendEmailAsync(_tenant, new SendEmailRequest { To = "", Subject = "s", Body = "b", CorrelationId = "c" });

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(EmailMediationFailureCodes.PlatformError);
        _email.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Send_TransportAcceptThrows_FailSoft_TypedPlatformError_No5xx()
    {
        _email.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp accept failed"));

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
