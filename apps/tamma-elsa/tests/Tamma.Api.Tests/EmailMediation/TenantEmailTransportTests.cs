using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.EmailMediation;
using Tamma.Api.Services.Integrations;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.EmailMediation;

/// <summary>
/// <see cref="TenantEmailTransport"/> — the SaaS BYOK transport. Pins that a
/// tenant's message is delivered over the TENANT'S OWN sending authority:
/// <list type="bullet">
///   <item>resend ⇒ POST carries <c>Authorization: Bearer &lt;TENANT key&gt;</c> and
///     <c>from = &lt;TENANT From&gt;</c> (never a platform key) + EMAIL.SENT audit.</item>
///   <item>smtp ⇒ the per-tenant <see cref="ITenantSmtpTransport"/> is invoked with
///     the TENANT'S relay credentials + EMAIL.SENT audit.</item>
///   <item>transport failure ⇒ EMAIL.SENT.FAILED audit + txn still returned, never
///     throws.</item>
/// </list>
/// </summary>
[TestFixture]
public class TenantEmailTransportTests
{
    private const string TenantResendKey = "re_tenant_secret_key";
    private const string TenantFrom = "team@tenant.example.com";

    private InMemoryDbFixture _fx = null!;
    private ControlPlaneDbContext _db = null!;
    private EventRepository _events = null!;
    private TenantContext _tenantContext = null!;
    private readonly Guid _tenant = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _fx = new InMemoryDbFixture();
        _tenantContext = new TenantContext();
        _db = _fx.Cp;
        _events = new EventRepository(_fx.Factory, _tenantContext, new PlatformEventRepository(_db));
    }

    [TearDown]
    public async Task TearDown() => await _fx.DisposeAsync();

    private static EmailMessage Message() => new(
        To: "dev@example.com", Subject: "Build passed", Html: "<p>green</p>", Text: "green",
        From: TenantFrom, Template: "ci", TenantId: null);

    private EmailMessage TenantMessage() => Message() with { TenantId = _tenant };

    // ── resend ────────────────────────────────────────────────────────────

    [Test]
    public async Task Resend_UsesTenantKeyAndFrom_EmitsSent_ReturnsTxn()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                captured = req;
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"x"}""") });

        var sut = BuildSut(handler);
        var cred = new EmailCredential(EmailCredential.TransportResend, TenantFrom, ResendApiKey: TenantResendKey);

        var txn = await sut.SendAsync(cred, TenantMessage());

        txn.Should().NotBe(Guid.Empty);
        // The tenant's OWN key authorizes the send — not a platform key.
        captured!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be(TenantResendKey);
        // The From on the wire is the tenant's identity.
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(capturedBody!)!;
        payload["from"].GetString().Should().Be(TenantFrom);

        var sent = await _events.QueryAsync(_tenant, EmailEventTypes.Sent, null, 10);
        sent.Should().ContainSingle();
    }

    [Test]
    public async Task Resend_Non2xx_EmitsFailed_StillReturnsTxn_NoThrow()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Forbidden));

        var sut = BuildSut(handler);
        var cred = new EmailCredential(EmailCredential.TransportResend, TenantFrom, ResendApiKey: TenantResendKey);

        var txn = await sut.SendAsync(cred, TenantMessage());

        txn.Should().NotBe(Guid.Empty);
        (await _events.QueryAsync(_tenant, EmailEventTypes.Failed, null, 10)).Should().ContainSingle();
        (await _events.QueryAsync(_tenant, EmailEventTypes.Sent, null, 10)).Should().BeEmpty();
    }

    // ── smtp ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Smtp_InvokesTenantSmtpTransportWithTenantCreds_EmitsSent()
    {
        var smtp = new Mock<ITenantSmtpTransport>(MockBehavior.Strict);
        EmailCredential? capturedCred = null;
        smtp.Setup(s => s.SendAsync(It.IsAny<EmailCredential>(), It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailCredential, EmailMessage, CancellationToken>((c, _, _) => capturedCred = c)
            .Returns(Task.CompletedTask);

        var sut = BuildSut(new Mock<HttpMessageHandler>(), smtp);
        var cred = new EmailCredential(EmailCredential.TransportSmtp, TenantFrom,
            SmtpHost: "smtp.tenant.example", SmtpPort: 587, SmtpUsername: "u", SmtpPassword: "p");

        var txn = await sut.SendAsync(cred, TenantMessage());

        txn.Should().NotBe(Guid.Empty);
        capturedCred.Should().BeSameAs(cred, "the tenant's own SMTP relay credentials must be used");
        (await _events.QueryAsync(_tenant, EmailEventTypes.Sent, null, 10)).Should().ContainSingle();
        smtp.VerifyAll();
    }

    [Test]
    public async Task Smtp_TransportThrows_EmitsFailed_StillReturnsTxn_NoThrow()
    {
        var smtp = new Mock<ITenantSmtpTransport>();
        smtp.Setup(s => s.SendAsync(It.IsAny<EmailCredential>(), It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("relay refused"));

        var sut = BuildSut(new Mock<HttpMessageHandler>(), smtp);
        var cred = new EmailCredential(EmailCredential.TransportSmtp, TenantFrom, SmtpHost: "smtp.tenant.example");

        var txn = await sut.SendAsync(cred, TenantMessage());

        txn.Should().NotBe(Guid.Empty);
        (await _events.QueryAsync(_tenant, EmailEventTypes.Failed, null, 10)).Should().ContainSingle();
    }

    private TenantEmailTransport BuildSut(Mock<HttpMessageHandler> handler, Mock<ITenantSmtpTransport>? smtp = null)
    {
        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.resend.com/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("resend")).Returns(client);
        return new TenantEmailTransport(
            factory.Object,
            (smtp ?? new Mock<ITenantSmtpTransport>()).Object,
            _events,
            NullLogger<TenantEmailTransport>.Instance);
    }
}
