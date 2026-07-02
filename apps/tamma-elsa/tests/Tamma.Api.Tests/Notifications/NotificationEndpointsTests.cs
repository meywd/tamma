using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Core.Interfaces;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Notifications;

/// <summary>
/// Story 38-3 (AC1/AC2) — HTTP tests for <c>POST /api/v1/notifications/slack</c>
/// via <see cref="WebApplicationFactory{TEntryPoint}"/>. Same engine-only plane
/// as <c>/api/v1/llm/call</c>: a missing/invalid bearer ⇒ 401, a non-engine (user)
/// principal ⇒ 403 — both BEFORE the handler. The outbox
/// (<see cref="ISlackOutboxRepository"/>) is a capturing fake so these tests
/// exercise ONLY the endpoint: auth, the auth-derived tenant scope (never the
/// body), the intent-write, the 202 envelope, and that Slack is NEVER called
/// synchronously in the request path.
/// </summary>
[TestFixture]
public class NotificationEndpointsTests
{
    private const string TestBearer = "engine-callback-token";
    private const string UserBearer = "tenant-user-token";
    private const string Route = "/api/v1/notifications/slack";

    private WebApplicationFactory<Program> _factory = null!;
    private CapturingSlackOutboxRepository _outbox = null!;
    private Mock<ISlackIntegrationService> _slack = null!;
    private StubTenantContext _tenantContext = null!;

    [SetUp]
    public void SetUp()
    {
        _outbox = new CapturingSlackOutboxRepository();
        _slack = new Mock<ISlackIntegrationService>(MockBehavior.Strict);
        _tenantContext = new StubTenantContext();

        _factory = ApiTestFixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.DisableAlertHostedServices();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISlackOutboxRepository>();
                services.AddScoped<ISlackOutboxRepository>(_ => _outbox);

                // Strict mock — any synchronous Slack call in the request path fails the test.
                services.RemoveAll<ISlackIntegrationService>();
                services.AddScoped<ISlackIntegrationService>(_ => _slack.Object);

                services.RemoveAll<ITenantContext>();
                services.AddScoped<ITenantContext>(_ => _tenantContext);

                services.AddAuthentication(TestEngineAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestEngineAuthHandler>(
                        TestEngineAuthHandler.SchemeName, _ => { });

                services.AddHttpContextAccessor();
                services.AddSingleton<IAuthorizationHandler, ServicePrincipalHandler>();

                services.AddAuthorization(options =>
                {
                    options.DefaultPolicy = new AuthorizationPolicyBuilder()
                        .AddAuthenticationSchemes(TestEngineAuthHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .Build();
                    options.AddPolicy("EngineServiceOnly", p =>
                    {
                        p.AddAuthenticationSchemes(TestEngineAuthHandler.SchemeName);
                        p.RequireAuthenticatedUser();
                        p.AddRequirements(new ServicePrincipalRequirement());
                    });
                });
            });
        });
    }

    [TearDown]
    public void TearDown() => _factory?.Dispose();

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestBearer);
        return client;
    }

    private static object ChannelBody() => new
    {
        action = "SendChannel",
        channel = "eng-updates",
        message = ":information_source: build green",
        messageType = "Info",
    };

    // -----------------------------------------------------------------------
    // AC1 — auth
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_NoBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync(Route, ChannelBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _outbox.Enqueued.Should().BeEmpty("the handler must not run when the bearer is missing");
    }

    [Test]
    public async Task Post_InvalidBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        var resp = await client.PostAsJsonAsync(Route, ChannelBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _outbox.Enqueued.Should().BeEmpty();
    }

    [Test]
    public async Task Post_NonEnginePrincipal_Returns403_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserBearer);
        var resp = await client.PostAsJsonAsync(Route, ChannelBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _outbox.Enqueued.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // AC2 — writes intent, returns 202, never calls Slack synchronously
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_ValidBearer_WritesPendingRow_Returns202_And_DoesNotCallSlack()
    {
        var tenant = Guid.NewGuid();
        _tenantContext.SetTenantId(tenant);

        using var client = AuthedClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        var resp = await client.PostAsJsonAsync(Route, new
        {
            action = "SendDirect",
            userId = "U123",
            message = ":x: something failed",
            messageType = "Error",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("outboxId").GetGuid().Should().NotBe(Guid.Empty);

        _outbox.Enqueued.Should().ContainSingle();
        var row = _outbox.Enqueued[0];
        row.Status.Should().Be("pending");
        row.TenantId.Should().Be(tenant, "the acting tenant comes from ITenantContext, not the body");
        row.UserId.Should().BeNull();
        row.TargetUserId.Should().Be("U123");
        row.Channel.Should().BeNull();
        row.MessageType.Should().Be("Error");
        row.Body.Should().Be(":x: something failed");

        // AC2 — the endpoint never posts to Slack synchronously (strict mock: any
        // call would have thrown before we got here).
        _slack.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Post_SingleUser_NoTenantHeader_WritesRowWithNullTenant()
    {
        // No X-Tenant-Id → single-user/platform scope; the row carries TenantId null.
        using var client = AuthedClient();

        var resp = await client.PostAsJsonAsync(Route, ChannelBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _outbox.Enqueued.Should().ContainSingle();
        _outbox.Enqueued[0].TenantId.Should().BeNull();
        _outbox.Enqueued[0].Channel.Should().Be("eng-updates");
        _slack.VerifyNoOtherCalls();
    }

    // -----------------------------------------------------------------------
    // FIX 1 — a both-targets intent splits into two independent single-target rows
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_BothTargets_WritesTwoRows_OneChannelOnly_OneDmOnly()
    {
        using var client = AuthedClient();

        var resp = await client.PostAsJsonAsync(Route, new
        {
            action = "SendNotification",
            channel = "eng-updates",
            userId = "U123",
            message = ":warning: heads up",
            messageType = "Warning",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("outboxIds").GetArrayLength().Should().Be(2);

        _outbox.Enqueued.Should().HaveCount(2);
        _outbox.Enqueued.Should().ContainSingle(
            r => r.Channel == "eng-updates" && r.TargetUserId == null,
            "the channel leg is a channel-only row");
        _outbox.Enqueued.Should().ContainSingle(
            r => r.TargetUserId == "U123" && r.Channel == null,
            "the DM leg is a DM-only row");
        _slack.VerifyNoOtherCalls();
    }

    // -----------------------------------------------------------------------
    // FIX 3 — both targets blank is rejected before any row is written
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_NoChannelAndNoUser_Returns400_NoRowWritten()
    {
        using var client = AuthedClient();

        var resp = await client.PostAsJsonAsync(Route, new
        {
            action = "SendNotification",
            message = ":x: orphaned",
            messageType = "Error",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _outbox.Enqueued.Should().BeEmpty("a row with no target could only burn every retry");
    }

    // -----------------------------------------------------------------------
    // FIX 5 — an oversized body is truncated at the cap before the row is written
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_OversizedMessage_TruncatesBodyToCap()
    {
        using var client = AuthedClient();

        var resp = await client.PostAsJsonAsync(Route, new
        {
            action = "SendChannel",
            channel = "eng-updates",
            message = new string('x', 10_000),
            messageType = "Info",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _outbox.Enqueued.Should().ContainSingle();
        var row = _outbox.Enqueued[0];
        row.Body.Length.Should().BeLessThanOrEqualTo(4000);
        row.Body.Should().EndWith("[truncated]");
    }

    // -----------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------

    private sealed class CapturingSlackOutboxRepository : ISlackOutboxRepository
    {
        public List<SlackOutboxMessage> Enqueued { get; } = new();

        public Task<SlackOutboxMessage> EnqueueAsync(SlackOutboxMessage msg, CancellationToken ct = default)
        {
            msg.Id = Guid.NewGuid();
            msg.Status = "pending";
            Enqueued.Add(msg);
            return Task.FromResult(msg);
        }

        // The hosted sender is gated off in tests; return null so a stray poll no-ops.
        public Task<SlackOutboxMessage?> ClaimNextPendingAsync(DateTime now, CancellationToken ct = default)
            => Task.FromResult<SlackOutboxMessage?>(null);

        // The hosted sender is gated off in tests; no rows to reap.
        public Task<int> ReclaimStuckSendingAsync(DateTime now, TimeSpan leaseTimeout, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task MarkSentAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<SlackOutboxMessage?> MarkFailedAsync(Guid id, string error, TimeSpan? backoff, CancellationToken ct = default)
            => Task.FromResult<SlackOutboxMessage?>(null);

        public Task<SlackOutboxMessage?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Enqueued.FirstOrDefault(m => m.Id == id));

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubTenantContext : ITenantContext
    {
        private Guid? _tenantId;
        public Guid? TenantId => _tenantId;
        public void SetTenantId(Guid tenantId) => _tenantId = tenantId;
        public void ClearTenantId() => _tenantId = null;
    }

    private sealed class TestEngineAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestEngine";

        public TestEngineAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var header))
                return Task.FromResult(AuthenticateResult.NoResult());

            var value = header.ToString();
            if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(AuthenticateResult.NoResult());

            var token = value["Bearer ".Length..].Trim();

            if (string.Equals(token, TestBearer, StringComparison.Ordinal))
            {
                Context.SetAuthPrincipal(new ServiceAuthPrincipal(
                    KeyId: Guid.NewGuid(), ServiceName: "tamma-engine",
                    Permissions: Array.Empty<string>(), TenantId: null));
                var identity = new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "tamma-engine"), new Claim("scope", "service") },
                    SchemeName);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }

            if (string.Equals(token, UserBearer, StringComparison.Ordinal))
            {
                var identity = new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("role", "owner") },
                    SchemeName);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }

            return Task.FromResult(AuthenticateResult.Fail("Invalid token"));
        }
    }
}
