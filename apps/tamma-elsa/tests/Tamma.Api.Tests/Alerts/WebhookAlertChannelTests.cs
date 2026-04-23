using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Alerts;
using Tamma.Api.Services.Alerts.Channels;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Story 1.5-37 (Wave C.1) — unit tests for
/// <see cref="WebhookAlertChannel"/>. Covers:
/// <list type="bullet">
///   <item><description>HMAC-SHA256 signature header contract</description></item>
///   <item><description>X-Tamma-Alert-Id dedup header</description></item>
///   <item><description>2xx → success; non-2xx → failure with status</description></item>
///   <item><description>Missing URL / missing secret id → pre-request failure</description></item>
///   <item><description>HMAC computation matches a reference implementation</description></item>
/// </list>
/// </summary>
[TestFixture]
public class WebhookAlertChannelTests
{
    private StubHttpHandler _handler = null!;
    private IHttpClientFactory _httpFactory = null!;
    private StubSecretReader _secrets = null!;
    private WebhookAlertChannel _channel = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new StubHttpHandler();
        _httpFactory = new StubHttpFactory(_handler);
        _secrets = new StubSecretReader();
        _channel = new WebhookAlertChannel(
            _httpFactory, _secrets,
            new TestTimeProvider(DateTimeOffset.Parse("2026-04-23T12:00:00Z")),
            NullLogger<WebhookAlertChannel>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_SuccessResponse_ReturnsSuccessAndSendsSignedRequest()
    {
        var secretId = Guid.NewGuid();
        _secrets.Plaintext[secretId] = "shh-i-am-the-secret";
        _handler.Response = new HttpResponseMessage(HttpStatusCode.OK);

        var alert = NewAlert();
        var channel = new AlertChannel
        {
            Id = Guid.NewGuid(),
            Name = "Ops Webhook",
            ChannelType = AlertChannelType.Webhook,
            Config = """{"url":"https://hooks.example.com/alert"}""",
            IsEnabled = true,
            CredentialsSecretId = secretId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var result = await _channel.SendAsync(alert, channel, default);
        result.Success.Should().BeTrue();

        _handler.LastRequest.Should().NotBeNull();
        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.ToString()
            .Should().Be("https://hooks.example.com/alert");

        // X-Tamma-Alert-Id = alert guid
        _handler.LastRequest.Headers.TryGetValues(
                "X-Tamma-Alert-Id", out var idValues).Should().BeTrue();
        idValues!.Single().Should().Be(alert.Id.ToString("D"));

        // X-Tamma-Signature = sha256=<hex> of the body
        _handler.LastRequest.Headers.TryGetValues(
                "X-Tamma-Signature", out var sigValues).Should().BeTrue();
        var signature = sigValues!.Single();
        signature.Should().StartWith("sha256=");

        var expectedHex = ReferenceHmacHex(_handler.LastRequestBody!,
            "shh-i-am-the-secret");
        signature.Should().Be($"sha256={expectedHex}");

        _handler.LastRequestBody.Should().Contain(alert.Title);
    }

    [Test]
    public async Task SendAsync_HttpNon2xx_ReturnsFailureWithStatus()
    {
        var secretId = Guid.NewGuid();
        _secrets.Plaintext[secretId] = "secret";
        _handler.Response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream 502"),
        };

        var result = await _channel.SendAsync(
            NewAlert(),
            new AlertChannel
            {
                Id = Guid.NewGuid(),
                Name = "w",
                ChannelType = AlertChannelType.Webhook,
                Config = """{"url":"https://x.io/w"}""",
                IsEnabled = true,
                CredentialsSecretId = secretId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("502");
    }

    [Test]
    public async Task SendAsync_MissingUrl_ReturnsFailureWithoutCallingHttp()
    {
        var result = await _channel.SendAsync(
            NewAlert(),
            new AlertChannel
            {
                Id = Guid.NewGuid(),
                Name = "w",
                ChannelType = AlertChannelType.Webhook,
                Config = "{}",
                IsEnabled = true,
                CredentialsSecretId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("url");
        _handler.LastRequest.Should().BeNull();
    }

    [Test]
    public async Task SendAsync_MissingCredentialsSecretId_Fails()
    {
        var result = await _channel.SendAsync(
            NewAlert(),
            new AlertChannel
            {
                Id = Guid.NewGuid(),
                Name = "w",
                ChannelType = AlertChannelType.Webhook,
                Config = """{"url":"https://x.io/w"}""",
                IsEnabled = true,
                CredentialsSecretId = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("CredentialsSecretId");
    }

    [Test]
    public void ComputeSignature_IsHmacSha256HexLowercase()
    {
        var sig = WebhookAlertChannel.ComputeSignature(
            body: "hello",
            sharedSecret: "abc");
        // Independent reference — HMAC-SHA256("abc", "hello")
        sig.Should().MatchRegex("^[0-9a-f]{64}$");
        sig.Should().Be(ReferenceHmacHex("hello", "abc"));
    }

    private static string ReferenceHmacHex(string body, string key)
    {
        var k = Encoding.UTF8.GetBytes(key);
        var b = Encoding.UTF8.GetBytes(body);
        var hash = HMACSHA256.HashData(k, b);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Alert NewAlert() => new()
    {
        Id = Guid.NewGuid(),
        Severity = AlertSeverity.Critical,
        Title = "Webhook alert",
        Description = "payload",
        CreatedAt = DateTime.UtcNow,
    };

    // ── Test doubles ────────────────────────────────────────────

    internal sealed class StubSecretReader : IAlertChannelSecretReader
    {
        public Dictionary<Guid, string?> Plaintext { get; } = new();
        public Task<string?> GetPlaintextAsync(Guid secretId, CancellationToken ct)
        {
            if (!Plaintext.TryGetValue(secretId, out var pt))
                throw new KeyNotFoundException(secretId.ToString("D"));
            return Task.FromResult(pt);
        }
    }

    internal sealed class StubHttpHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } =
            new(HttpStatusCode.OK);
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content
                    .ReadAsStringAsync(cancellationToken);
            }
            return Response;
        }
    }

    internal sealed class StubHttpFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
