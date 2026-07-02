using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.EmailMediation;

namespace Tamma.Api.Tests.EmailMediation;

/// <summary>
/// Story 38 (Phase 1) — <see cref="EmailMediationService"/> composition. Email is not
/// repo-scoped: it accepts the engine's rendered message into the credentialed,
/// outbox-backed <see cref="IEmailService"/> (which owns transport + EMAIL.* audit)
/// under the acting tenant. Fail-soft: a missing recipient or an accept exception
/// surfaces a typed PLATFORM_ERROR inside a 200 success:false envelope (never a raw
/// 5xx). The email credential never reaches the engine.
/// </summary>
[TestFixture]
public class EmailMediationServiceTests
{
    private Mock<IEmailService> _email = null!;
    private EmailMediationService _sut = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _email = new Mock<IEmailService>(MockBehavior.Strict);
        _sut = new EmailMediationService(_email.Object, NullLogger<EmailMediationService>.Instance);
    }

    private static SendEmailRequest Body() => new()
    {
        To = "dev@example.com", Subject = "Build passed", Body = "All green", CorrelationId = "corr-e",
    };

    [Test]
    public async Task Send_Success_AcceptsIntoOutbox_ReturnsTxnId_ScopesTenant()
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
        captured!.TenantId.Should().Be(_tenant, "the acting tenant scopes the message + its EMAIL.* events");
        captured.To.Should().Be("dev@example.com");
        captured.Text.Should().Be("All green");
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
}
