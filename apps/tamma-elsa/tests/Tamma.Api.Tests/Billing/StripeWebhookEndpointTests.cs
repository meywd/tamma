using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Endpoints.Billing;
using Tamma.Api.Services.Billing;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-5 (AC1–AC3) — <see cref="StripeWebhookEndpoint"/> control flow, tested
/// directly against a <see cref="DefaultHttpContext"/> with mocked services (no
/// host/docker): missing signature → 400; unresolvable secret → 503 (fail closed);
/// invalid signature → 400 (never projects); valid → 200 + processor invoked once.
/// </summary>
[TestFixture]
public class StripeWebhookEndpointTests
{
    private static readonly ILoggerFactory Loggers = NullLoggerFactory.Instance;

    private static DefaultHttpContext MakeContext(string body, string? signature)
    {
        var ctx = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        if (signature is not null)
            ctx.Request.Headers["Stripe-Signature"] = signature;
        return ctx;
    }

    private static int StatusOf(IResult result) =>
        ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    [Test]
    public async Task Missing_Signature_Returns_400_And_Never_Processes()
    {
        var secret = new Mock<IStripeSigningSecretSource>();
        var verifier = new Mock<IStripeEventVerifier>();
        var processor = new Mock<IStripeWebhookProcessor>();

        var result = await StripeWebhookEndpoint.Receive(
            MakeContext("{}", signature: null),
            secret.Object, verifier.Object, processor.Object, Loggers);

        StatusOf(result).Should().Be(400);
        processor.Verify(p => p.ProcessAsync(
            It.IsAny<Stripe.Event>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Unresolvable_Secret_Returns_503_And_Never_Processes()
    {
        var secret = new Mock<IStripeSigningSecretSource>();
        secret.Setup(s => s.GetSigningSecretAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var verifier = new Mock<IStripeEventVerifier>();
        var processor = new Mock<IStripeWebhookProcessor>();

        var result = await StripeWebhookEndpoint.Receive(
            MakeContext("{}", signature: "t=1,v1=abc"),
            secret.Object, verifier.Object, processor.Object, Loggers);

        StatusOf(result).Should().Be(503);
        verifier.Verify(v => v.Construct(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never, "never verify (or process) when the secret is unresolvable");
        processor.Verify(p => p.ProcessAsync(
            It.IsAny<Stripe.Event>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Invalid_Signature_Returns_400_And_Never_Processes()
    {
        var secret = new Mock<IStripeSigningSecretSource>();
        secret.Setup(s => s.GetSigningSecretAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("whsec_test");
        var verifier = new Mock<IStripeEventVerifier>();
        verifier.Setup(v => v.Construct(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new Stripe.StripeException("signature verification failed"));
        var processor = new Mock<IStripeWebhookProcessor>();

        var result = await StripeWebhookEndpoint.Receive(
            MakeContext("{}", signature: "t=1,v1=bad"),
            secret.Object, verifier.Object, processor.Object, Loggers);

        StatusOf(result).Should().Be(400);
        processor.Verify(p => p.ProcessAsync(
            It.IsAny<Stripe.Event>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "an invalid signature never projects");
    }

    [Test]
    public async Task Valid_Signature_Returns_200_And_Processes_Once()
    {
        var secret = new Mock<IStripeSigningSecretSource>();
        secret.Setup(s => s.GetSigningSecretAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("whsec_test");
        var evt = new Stripe.Event { Id = "evt_ok", Type = "invoice.paid" };
        var verifier = new Mock<IStripeEventVerifier>();
        verifier.Setup(v => v.Construct(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(evt);
        var processor = new Mock<IStripeWebhookProcessor>();
        processor.Setup(p => p.ProcessAsync(evt, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebhookProcessResult.Projected);

        var result = await StripeWebhookEndpoint.Receive(
            MakeContext("""{"id":"evt_ok"}""", signature: "t=1,v1=good"),
            secret.Object, verifier.Object, processor.Object, Loggers);

        StatusOf(result).Should().Be(200);
        processor.Verify(p => p.ProcessAsync(evt, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
