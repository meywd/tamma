using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Webhooks;

/// <summary>
/// Epic 31 P4 M4 — the legacy <c>/api/github/webhooks</c> route enters its
/// promised deprecation window with CROSS-ROUTE idempotency: during dual-route
/// operation both routes gate processing on the SAME
/// <c>platform_webhook_deliveries</c> row (kind=github), so one GitHub
/// delivery id hitting BOTH routes processes exactly once — in either order.
/// </summary>
[TestFixture]
public class LegacyWebhookCrossRouteTests
{
    private const string Secret = "cross-route-secret";

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
    }

    private static WebApplicationFactory<Program> ConfigureFactory(
        List<PlatformWebhookEvent> captured)
    {
        return ApiTestFixture.Factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Same secret on both routes — the realistic dual-route
                    // deployment (one GitHub App webhook config).
                    ["GitHub:WebhookSecret"] = Secret,
                    ["Webhooks:Secrets:github"] = Secret,
                });
            });
            b.ConfigureServices(services =>
            {
                services.AddSingleton<IWebhookEventDispatcher>(
                    _ => new RecordingDispatcher(captured));
            });
        });
    }

    private static string SignHmac(byte[] body) =>
        "sha256=" + Convert.ToHexString(
            new HMACSHA256(Encoding.UTF8.GetBytes(Secret)).ComputeHash(body)).ToLowerInvariant();

    private static HttpRequestMessage Request(string path, string deliveryId, byte[] body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-GitHub-Event", "pull_request");
        req.Headers.Add("X-GitHub-Delivery", deliveryId);
        req.Headers.Add("X-Hub-Signature-256", SignHmac(body));
        return req;
    }

    private static readonly byte[] Body = Encoding.UTF8.GetBytes(
        """{"action":"closed","number":9,"pull_request":{"number":9,"merged":false},"installation":{"id":42}}""");

    [Test]
    public async Task OneDeliveryId_LegacyThenPlatformRoute_ProcessesOnce()
    {
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(captured);
        using var client = factory.CreateClient();
        var deliveryId = Guid.NewGuid().ToString();

        // 1) Legacy route processes (and claims the shared idempotency row).
        var legacy = await client.SendAsync(Request("/api/github/webhooks", deliveryId, Body));
        legacy.StatusCode.Should().Be(HttpStatusCode.OK);
        (await legacy.Content.ReadAsStringAsync()).Should().NotContain("duplicate_delivery");

        // 2) The SAME delivery replayed on the platform route is a duplicate.
        var platform = await client.SendAsync(Request("/api/webhooks/github", deliveryId, Body));
        platform.StatusCode.Should().Be(HttpStatusCode.OK);
        (await platform.Content.ReadAsStringAsync()).Should().Contain("duplicate_delivery");
        captured.Should().BeEmpty("the platform dispatcher must not re-process a legacy-claimed delivery");
    }

    [Test]
    public async Task OneDeliveryId_PlatformThenLegacyRoute_ProcessesOnce()
    {
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(captured);
        using var client = factory.CreateClient();
        var deliveryId = Guid.NewGuid().ToString();

        // 1) Platform route processes (dispatches + claims the shared row).
        var platform = await client.SendAsync(Request("/api/webhooks/github", deliveryId, Body));
        platform.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().HaveCount(1);

        // 2) The SAME delivery replayed on the LEGACY route is a duplicate —
        //    the legacy route now also gates on the platform delivery table.
        var legacy = await client.SendAsync(Request("/api/github/webhooks", deliveryId, Body));
        legacy.StatusCode.Should().Be(HttpStatusCode.OK);
        (await legacy.Content.ReadAsStringAsync()).Should().Contain("duplicate_delivery");
    }

    [Test]
    public async Task LegacyRoute_AdvertisesDeprecation_WithSuccessorLink()
    {
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(captured);
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(
            Request("/api/github/webhooks", Guid.NewGuid().ToString(), Body));

        resp.Headers.TryGetValues("Deprecation", out var deprecation).Should().BeTrue();
        deprecation!.Single().Should().Be("true");
        resp.Headers.TryGetValues("Link", out var link).Should().BeTrue();
        link!.Single().Should().Contain("/api/webhooks/github");
    }

    private sealed class RecordingDispatcher : IWebhookEventDispatcher
    {
        private readonly List<PlatformWebhookEvent> _captured;
        public RecordingDispatcher(List<PlatformWebhookEvent> captured) => _captured = captured;
        public int HandlerCount => 0;
        public void RegisterHandler(IWebhookHandler handler) { }
        public Task<int> DispatchAsync(PlatformWebhookEvent evt, CancellationToken ct = default)
        {
            _captured.Add(evt);
            return Task.FromResult(1);
        }
    }
}
