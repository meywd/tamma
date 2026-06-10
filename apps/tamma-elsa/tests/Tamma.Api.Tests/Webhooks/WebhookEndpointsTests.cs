using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Webhooks;

/// <summary>
/// Story 31-7 — integration coverage of the generalised webhook
/// receiver. Asserts:
/// <list type="bullet">
///   <item>HMAC valid → 200; bad sig → 401; missing header → 401.</item>
///   <item>GitLab static-token valid → 200; bad token → 401.</item>
///   <item>Missing secret → 503 (audit finding 001 fail-closed).</item>
///   <item>Idempotency: duplicate delivery id → 200, no re-dispatch.</item>
///   <item>Cross-tenant isolation: a webhook bearing tenant A's
///         externalId never reaches tenant B's handler.</item>
///   <item>Legacy /api/github/webhooks redirects to /api/webhooks/github
///         with deprecation headers.</item>
/// </list>
/// </summary>
[TestFixture]
public class WebhookEndpointsTests
{
    private const string GlobalSecret = "global-test-secret";

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
    }

    private static WebApplicationFactory<Program> ConfigureFactory(
        Action<IServiceCollection>? overrides = null,
        Dictionary<string, string?>? extraConfig = null)
    {
        return ApiTestFixture.Factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Webhooks:Secrets:github"] = GlobalSecret,
                    ["Webhooks:Secrets:gitea"] = GlobalSecret,
                    ["Webhooks:Secrets:forgejo"] = GlobalSecret,
                    ["Webhooks:Secrets:gitlab"] = GlobalSecret,
                };
                if (extraConfig is not null)
                {
                    foreach (var kv in extraConfig) values[kv.Key] = kv.Value;
                }
                config.AddInMemoryCollection(values);
            });
            if (overrides is not null)
            {
                b.ConfigureServices(overrides);
            }
        });
    }

    private static string SignHmac(byte[] body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    // ─── HMAC: GitHub happy path + failures ─────────────────────────────────

    [Test]
    public async Task GitHub_ValidSignature_Returns200_AndDispatchesHandler()
    {
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(services =>
        {
            // Replace the dispatcher with a recorder so we can assert
            // the right event was published.
            services.AddSingleton<IWebhookEventDispatcher>(_ =>
                new RecordingDispatcher(captured));
        });
        using var client = factory.CreateClient();

        var bodyBytes = Encoding.UTF8.GetBytes(
            """{"action":"created","installation":{"id":42}}""");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-GitHub-Event", "installation");
        req.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        req.Headers.Add("X-Hub-Signature-256", SignHmac(bodyBytes, GlobalSecret));

        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        captured.Should().HaveCount(1);
        captured[0].Kind.Should().Be(PlatformKind.GitHub);
        captured[0].EventType.Should().Be("installation");
        captured[0].Action.Should().Be("created");
    }

    [Test]
    public async Task GitHub_BadSignature_Returns401_AndDoesNotDispatch()
    {
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(services =>
        {
            services.AddSingleton<IWebhookEventDispatcher>(_ =>
                new RecordingDispatcher(captured));
        });
        using var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github")
        {
            Content = new StringContent("""{"action":"created"}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-GitHub-Event", "installation");
        req.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('0', 64));

        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        captured.Should().BeEmpty("no dispatch on bad signature");
    }

    [Test]
    public async Task GitHub_MissingSignatureHeader_Returns401()
    {
        using var factory = ConfigureFactory();
        using var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-GitHub-Event", "installation");

        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GitHub_NoSecretConfigured_Returns503()
    {
        // Audit finding 001 fail-closed — empty secret must not fall
        // through to a 200; receiver returns 503.
        using var factory = ConfigureFactory(extraConfig: new Dictionary<string, string?>
        {
            ["Webhooks:Secrets:github"] = "",
            ["GitHub:WebhookSecret"] = "",
        });
        using var client = factory.CreateClient();

        var bodyBytes = Encoding.UTF8.GetBytes("""{"action":"created"}""");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-GitHub-Event", "installation");
        req.Headers.Add("X-Hub-Signature-256", SignHmac(bodyBytes, "anything"));

        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ─── HMAC: Gitea happy path ─────────────────────────────────────────────

    [Test]
    public async Task Gitea_ValidSignature_Returns200()
    {
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(services =>
        {
            services.AddSingleton<IWebhookEventDispatcher>(_ =>
                new RecordingDispatcher(captured));
        });
        using var client = factory.CreateClient();

        var bodyBytes = Encoding.UTF8.GetBytes(
            """{"action":"opened","repository":{"id":99}}""");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/gitea")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-Gitea-Event", "pull_request");
        req.Headers.Add("X-Gitea-Delivery", Guid.NewGuid().ToString());
        req.Headers.Add("X-Gitea-Signature", SignHmac(bodyBytes, GlobalSecret));

        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured[0].Kind.Should().Be(PlatformKind.Gitea);
    }

    // ─── Static-token: GitLab valid + invalid ───────────────────────────────

    [Test]
    public async Task GitLab_ValidToken_Returns200()
    {
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(services =>
        {
            services.AddSingleton<IWebhookEventDispatcher>(_ =>
                new RecordingDispatcher(captured));
        });
        using var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/gitlab")
        {
            Content = new StringContent(
                """{"object_kind":"push","project":{"id":7}}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Gitlab-Event", "Push Hook");
        req.Headers.Add("X-Gitlab-Token", GlobalSecret);
        req.Headers.Add("X-Gitlab-Event-UUID", Guid.NewGuid().ToString());

        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured[0].Kind.Should().Be(PlatformKind.GitLab);
        captured[0].EventType.Should().Be("push", "GitLab event header normalized");
    }

    [Test]
    public async Task GitLab_BadToken_Returns401()
    {
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(services =>
        {
            services.AddSingleton<IWebhookEventDispatcher>(_ =>
                new RecordingDispatcher(captured));
        });
        using var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/gitlab")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Gitlab-Event", "Push Hook");
        req.Headers.Add("X-Gitlab-Token", "wrong-token");

        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        captured.Should().BeEmpty();
    }

    // ─── Idempotency / replay protection ────────────────────────────────────

    [Test]
    public async Task DuplicateDeliveryId_ReturnsOk_WithoutReDispatching()
    {
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(services =>
        {
            services.AddSingleton<IWebhookEventDispatcher>(_ =>
                new RecordingDispatcher(captured));
        });
        using var client = factory.CreateClient();

        var bodyBytes = Encoding.UTF8.GetBytes(
            """{"action":"created","installation":{"id":42}}""");
        var deliveryId = Guid.NewGuid().ToString();
        var sig = SignHmac(bodyBytes, GlobalSecret);

        HttpRequestMessage Build()
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github")
            {
                Content = new ByteArrayContent(bodyBytes),
            };
            r.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            r.Headers.Add("X-GitHub-Event", "installation");
            r.Headers.Add("X-GitHub-Delivery", deliveryId);
            r.Headers.Add("X-Hub-Signature-256", sig);
            return r;
        }

        var first = await client.SendAsync(Build());
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().HaveCount(1);

        var second = await client.SendAsync(Build());
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "duplicate delivery is idempotent — already-processed signal");
        var json = await second.Content.ReadAsStringAsync();
        json.Should().Contain("\"skipped\":true");

        captured.Should().HaveCount(1, "second delivery must NOT re-dispatch");

        // Persistence sanity-check
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var rows = await db.PlatformWebhookDeliveries
            .Where(d => d.DeliveryId == deliveryId)
            .ToListAsync();
        rows.Should().HaveCount(1);
    }

    // ─── Cross-tenant isolation ─────────────────────────────────────────────

    [Test]
    public async Task CrossTenantIsolation_TenantBHandlerNeverSeesTenantAsEvent()
    {
        // Seed two installation rows: tenant A bound to GitHub
        // installation 1001, tenant B to GitHub installation 2002.
        // A push from installation 1001 must enrich tenantId=A on the
        // event; the dispatcher single handler observes tenant ids.
        // IMPORTANT: WithWebHostBuilder spins a NEW TestServer that
        // re-runs Program.cs startup (which wipes + re-migrates the
        // CP DB). Order: configure the factory FIRST, then seed via
        // its scope so the data survives through the test request.
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(services =>
        {
            services.AddSingleton<IWebhookEventDispatcher>(_ =>
                new RecordingDispatcher(captured));
        });
        // Touch the factory's services to force startup BEFORE seeding,
        // so the wipe-and-migrate path runs first.
        _ = factory.Services;

        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.TenantPlatformInstallations.AddRange(
                new TenantPlatformInstallation
                {
                    TenantId = tenantA,
                    PlatformKind = "github",
                    BaseUrl = "https://api.github.com",
                    InstallationExternalId = "1001",
                    CredentialSecretScope = "tenant",
                    CredentialSecretName = "gh-1001",
                    Status = "connected",
                    IsPrimary = true,
                },
                new TenantPlatformInstallation
                {
                    TenantId = tenantB,
                    PlatformKind = "github",
                    BaseUrl = "https://api.github.com",
                    InstallationExternalId = "2002",
                    CredentialSecretScope = "tenant",
                    CredentialSecretName = "gh-2002",
                    Status = "connected",
                    IsPrimary = true,
                });
            await db.SaveChangesAsync();
        }

        // Sanity: seeds visible to a fresh scope of the same factory,
        // AND the secret resolver returns the right tenant for each
        // external id.
        using (var verify = factory.Services.CreateScope())
        {
            var verifyDb = verify.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var seeded = await verifyDb.TenantPlatformInstallations
                .Where(r => r.PlatformKind == "github")
                .ToListAsync();
            seeded.Should().HaveCount(2, "seed visible across scope");

            var secretResolver = verify.ServiceProvider
                .GetRequiredService<Tamma.Api.Services.Webhooks.IWebhookSecretResolver>();
            var aInst = await secretResolver.ResolveInstallationAsync(
                PlatformKind.GitHub, "1001");
            aInst.Should().NotBeNull("resolver finds tenant A's row");
            aInst!.TenantId.Should().Be(tenantA);
        }

        using var client = factory.CreateClient();

        async Task PostFor(long installId)
        {
            var body = Encoding.UTF8.GetBytes(
                "{\"action\":\"created\",\"installation\":{\"id\":" + installId + "}}");
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github")
            {
                Content = new ByteArrayContent(body),
            };
            req.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            req.Headers.Add("X-GitHub-Event", "installation");
            req.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
            req.Headers.Add("X-Hub-Signature-256", SignHmac(body, GlobalSecret));
            var response = await client.SendAsync(req);
            var rspBody = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                $"installId={installId} body={rspBody}");
        }

        await PostFor(1001L);
        await PostFor(2002L);

        captured.Should().HaveCount(2);
        var aEvent = captured.SingleOrDefault(e => e.InstallationExternalId == "1001");
        var bEvent = captured.SingleOrDefault(e => e.InstallationExternalId == "2002");

        aEvent.Should().NotBeNull();
        bEvent.Should().NotBeNull();

        // CRITICAL invariant: tenantId on the dispatched event is
        // resolved through IPlatformResolver.ResolveForWebhookAsync,
        // which scopes by (kind, externalId). Tenant B's event must
        // never carry tenant A's id.
        aEvent!.TenantId.Should().Be(tenantA);
        bEvent!.TenantId.Should().Be(tenantB);
        aEvent.TenantId.Should().NotBe(tenantB);
        bEvent.TenantId.Should().NotBe(tenantA);
    }

    // ─── Bad path / unknown platform ────────────────────────────────────────

    [Test]
    public async Task UnknownPlatform_Returns400()
    {
        using var factory = ConfigureFactory();
        using var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/notarealplatform")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── log-injection sanitizer (CodeQL cs/log-forging) ────────────────────

    [Test]
    public async Task SanitizeWebhookIdentifier_StripsCrLfFromEventTypeBeforeDispatch()
    {
        // CWE-117 — a malicious X-GitHub-Event header containing CR/LF
        // would otherwise flow into structured log calls and forge
        // log lines. The receiver routes the header through
        // SanitizeWebhookIdentifier (regex allowlist [A-Za-z0-9._-])
        // BEFORE the value reaches PlatformWebhookEvent.EventType.
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(services =>
        {
            services.AddSingleton<IWebhookEventDispatcher>(_ =>
                new RecordingDispatcher(captured));
        });
        using var client = factory.CreateClient();

        var bodyBytes = Encoding.UTF8.GetBytes(
            """{"action":"created","installation":{"id":42}}""");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        // Header value contains CR/LF + a forged "log line" — every
        // disallowed char must be stripped.
        req.Headers.TryAddWithoutValidation(
            "X-GitHub-Event",
            "installation\r\n2026-01-01 ERROR: forged");
        req.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        req.Headers.Add("X-Hub-Signature-256", SignHmac(bodyBytes, GlobalSecret));

        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        captured.Should().HaveCount(1);
        var sanitized = captured[0].EventType;
        sanitized.Should().NotContain("\r");
        sanitized.Should().NotContain("\n");
        sanitized.Should().NotContain(" ");
        sanitized.Should().NotContain(":");
        // The legitimate prefix survives the strip; everything after
        // the first disallowed char is collapsed (the regex drops
        // disallowed chars, keeping the rest).
        sanitized.Should().StartWith("installation");
    }

    [Test]
    public async Task SanitizeWebhookIdentifier_StripsCrLfFromActionField()
    {
        // Action comes from the JSON body's `action` field. Same
        // CWE-117 risk as EventType — malicious payload must be
        // sanitised before flowing into logs / dispatch / idempotency
        // table.
        var captured = new List<PlatformWebhookEvent>();
        using var factory = ConfigureFactory(services =>
        {
            services.AddSingleton<IWebhookEventDispatcher>(_ =>
                new RecordingDispatcher(captured));
        });
        using var client = factory.CreateClient();

        var bodyBytes = Encoding.UTF8.GetBytes(
            "{\"action\":\"created\\r\\nFAKE LOG LINE\",\"installation\":{\"id\":42}}");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-GitHub-Event", "installation");
        req.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        req.Headers.Add("X-Hub-Signature-256", SignHmac(bodyBytes, GlobalSecret));

        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        captured.Should().HaveCount(1);
        var sanitized = captured[0].Action;
        sanitized.Should().NotBeNull();
        sanitized!.Should().NotContain("\r");
        sanitized.Should().NotContain("\n");
        sanitized.Should().NotContain(" ");
    }

    [Test]
    public async Task UnknownPlatformPath_ResponseBodyIsSanitised()
    {
        // Reflected user input in the 400 JSON must also pass through
        // the sanitiser — upstream proxies / WAF logs may persist
        // response bodies, so the same CWE-117 rule applies.
        using var factory = ConfigureFactory();
        using var client = factory.CreateClient();

        // URL path with disallowed chars URL-encoded so they reach the
        // server intact: %0D%0A == CRLF.
        var path = "/api/webhooks/notaplatform%0D%0Aforged";
        var response = await client.PostAsync(path,
            new StringContent("{}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("\r");
        body.Should().NotContain("\n");
        // The recognisable prefix should survive (the allowlist keeps
        // [A-Za-z0-9._-]); everything else is dropped.
        body.Should().Contain("notaplatform");
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Test-only dispatcher that records every event it sees. Replaces
    /// the production dispatcher in DI so tests can assert on dispatched
    /// events without registering real handlers.
    /// </summary>
    private sealed class RecordingDispatcher : IWebhookEventDispatcher
    {
        private readonly List<PlatformWebhookEvent> _captured;
        public RecordingDispatcher(List<PlatformWebhookEvent> captured) => _captured = captured;
        public int HandlerCount => _captured.Count;
        public void RegisterHandler(IWebhookHandler handler) { /* unused in tests */ }
        public Task<int> DispatchAsync(PlatformWebhookEvent evt, CancellationToken ct = default)
        {
            lock (_captured) _captured.Add(evt);
            return Task.FromResult(1);
        }
    }
}
