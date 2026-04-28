using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Tamma.Api.Services.Email;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Email;

/// <summary>
/// Unit tests for the Resend HTTP provider. All tests use a stubbed
/// <see cref="HttpMessageHandler"/> so we exercise the real HttpClient
/// pipeline without any network I/O.
///
/// <para>Verified behaviour:</para>
/// <list type="bullet">
///   <item><description>2xx → <c>EMAIL.SENT.SUCCESS</c> + txn id returned.</description></item>
///   <item><description>5xx → <c>EMAIL.SENT.FAILED</c> + txn id still returned.
///     No recipient appears in the log sink.</description></item>
///   <item><description>Network exception → <c>EMAIL.SENT.FAILED</c> + txn id
///     returned (does NOT throw to the caller).</description></item>
///   <item><description>Every path emits <c>EMAIL.QUEUED.SUCCESS</c> first.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class ResendEmailServiceTests
{
    private InMemoryDbFixture _fx = null!;
    private ControlPlaneDbContext _db = null!;
    private EventRepository _events = null!;
    private TenantContext _tenantContext = null!;
    private IConfiguration _config = null!;
    private CapturingLoggerProvider _loggerProvider = null!;
    private ILoggerFactory _loggerFactory = null!;
    private ILogger<ResendEmailService> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new InMemoryDbFixture();
        _tenantContext = new TenantContext();
        _db = _fx.Cp;
        _events = new EventRepository(
            _fx.Factory,
            _tenantContext,
            new PlatformEventRepository(_db));

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:From"] = "noreply@tamma.dev",
                ["Email:Resend:ApiKey"] = "re_test_key",
            })
            .Build();

        _loggerProvider = new CapturingLoggerProvider();
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_loggerProvider));
        _logger = _loggerFactory.CreateLogger<ResendEmailService>();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fx.DisposeAsync();
        _loggerFactory.Dispose();
        _loggerProvider.Dispose();
    }

    private static EmailMessage NewMessage()
        => new(
            To: "alice@example.com",
            Subject: "Verify your email",
            Html: "<p>hi</p>",
            Text: "hi",
            Template: "verification");

    private (ResendEmailService svc, Mock<HttpMessageHandler> handler) BuildService(
        HttpStatusCode status, string? responseBody = null, Exception? throwOnSend = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        var setup = handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        if (throwOnSend is not null)
        {
            setup.ThrowsAsync(throwOnSend);
        }
        else
        {
            var response = new HttpResponseMessage(status);
            if (responseBody is not null)
                response.Content = new StringContent(responseBody);
            setup.ReturnsAsync(response);
        }

        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.resend.com/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("resend")).Returns(client);

        var svc = new ResendEmailService(
            factory.Object, _events, _tenantContext, _config, _logger);
        return (svc, handler);
    }

    [Test]
    public async Task SendAsync_Http200_EmitsSentEventAndReturnsTxnId()
    {
        var (svc, _) = BuildService(HttpStatusCode.OK, responseBody: """{"id":"resend-123"}""");

        var txnId = await svc.SendAsync(NewMessage());

        txnId.Should().NotBe(Guid.Empty);

        var queued = await _events.QueryAsync(null, EmailEventTypes.Queued, null, 10);
        var sent = await _events.QueryAsync(null, EmailEventTypes.Sent, null, 10);
        var failed = await _events.QueryAsync(null, EmailEventTypes.Failed, null, 10);

        queued.Should().ContainSingle();
        sent.Should().ContainSingle();
        failed.Should().BeEmpty();

        JsonSerializer.Deserialize<Dictionary<string, string?>>(sent[0].Tags)!["txn_id"]
            .Should().Be(txnId.ToString());
    }

    [Test]
    public async Task SendAsync_Http500_EmitsFailedEvent_LogsTxnIdOnly_AndStillReturnsTxnId()
    {
        var (svc, _) = BuildService(HttpStatusCode.InternalServerError);

        var txnId = await svc.SendAsync(NewMessage());

        txnId.Should().NotBe(Guid.Empty);

        var queued = await _events.QueryAsync(null, EmailEventTypes.Queued, null, 10);
        var failed = await _events.QueryAsync(null, EmailEventTypes.Failed, null, 10);
        var sent = await _events.QueryAsync(null, EmailEventTypes.Sent, null, 10);

        queued.Should().ContainSingle();
        failed.Should().ContainSingle();
        sent.Should().BeEmpty();

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(failed[0].Data)!;
        data["provider"].GetString().Should().Be("resend");
        data["http_status"].GetInt32().Should().Be(500);

        // CRITICAL: no recipient in any log line for this provider/txn.
        _loggerProvider.Messages.Should().NotContain(m => m.Contains("alice@example.com"));
        _loggerProvider.Messages.Should().Contain(m => m.Contains(txnId.ToString()),
            "txn id is the single identifier in the failure log");
    }

    [Test]
    public async Task SendAsync_NetworkException_SwallowsAndEmitsFailedEvent()
    {
        var (svc, _) = BuildService(HttpStatusCode.OK,
            throwOnSend: new HttpRequestException("DNS lookup failed"));

        var act = async () => await svc.SendAsync(NewMessage());
        var result = await act.Should().NotThrowAsync("transport failures must not throw to caller");

        result.Subject.Should().NotBe(Guid.Empty);

        var failed = await _events.QueryAsync(null, EmailEventTypes.Failed, null, 10);
        failed.Should().ContainSingle();

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(failed[0].Data)!;
        data["error_class"].GetString().Should().Be(typeof(HttpRequestException).FullName);
    }

    [Test]
    public async Task SendAsync_MissingApiKey_EmitsFailedEvent_ReturnsTxnId()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:From"] = "noreply@tamma.dev",
                // Email:Resend:ApiKey deliberately omitted
            })
            .Build();

        var (svc, handler) = BuildService(HttpStatusCode.OK);
        // Rebuild service with the missing-key config
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("resend"))
            .Returns(new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.resend.com/") });
        svc = new ResendEmailService(factory.Object, _events, _tenantContext, _config, _logger);

        var txnId = await svc.SendAsync(NewMessage());

        txnId.Should().NotBe(Guid.Empty);

        var failed = await _events.QueryAsync(null, EmailEventTypes.Failed, null, 10);
        failed.Should().ContainSingle();
    }

    [Test]
    public async Task SendAsync_EventTagsNeverContainRecipientOrSubjectOrBody()
    {
        var (svc, _) = BuildService(HttpStatusCode.OK);

        await svc.SendAsync(NewMessage());

        var all = await _events.QueryAsync(null, null, null, 20);
        foreach (var evt in all)
        {
            var combined = evt.Tags + evt.Data;
            combined.Should().NotContain("alice@example.com");
            combined.Should().NotContain("Verify your email");
            combined.Should().NotContain("<p>hi</p>");
        }
    }
}
