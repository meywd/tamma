using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Endpoints;
using Tamma.Api.Hubs;
using Tamma.Api.Services.Channels;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Channels;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Channels;

/// <summary>
/// Story 39-18 (AC3/AC4/AC5 runtime/AC7) — SignalR hub integration over
/// <see cref="WebApplicationFactory{TEntryPoint}"/> (Testcontainers Postgres via the
/// shared <c>ApiTestFixture</c>). Uses an in-memory outbox fake + a test auth scheme
/// so the suite exercises the HUB mechanics deterministically: two-tenant isolation,
/// degraded mode (no consumer → the row waits), and connect-time replay-without-loss.
/// Docker-gated (the shared fixture boots a container; CI runs it).
/// </summary>
[TestFixture]
public class ChannelHubIntegrationTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private InMemoryChannelOutbox _outbox = null!;

    [SetUp]
    public void SetUp()
    {
        _outbox = new InMemoryChannelOutbox();
        _factory = ApiTestFixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChannelOutboxRepository>();
                services.AddSingleton<IChannelOutboxRepository>(_outbox);
                services.RemoveAll<IEventRepository>();
                services.AddScoped<IEventRepository, NoopEventRepository>();

                services.AddAuthentication(TestChannelAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestChannelAuthHandler>(TestChannelAuthHandler.Scheme, _ => { });
                services.AddHttpContextAccessor();
                services.AddScoped<IAuthorizationHandler, OrchestratorChannelHandler>();
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("OrchestratorChannel", p =>
                    {
                        p.AddAuthenticationSchemes(TestChannelAuthHandler.Scheme);
                        p.RequireAuthenticatedUser();
                        p.AddRequirements(new OrchestratorChannelRequirement());
                    });
                    options.AddPolicy("MemberAccess", p =>
                    {
                        p.AddAuthenticationSchemes(TestChannelAuthHandler.Scheme);
                        p.RequireAuthenticatedUser();
                    });
                });
            });
        });
    }

    [TearDown]
    public void TearDown() => _factory?.Dispose();

    private HubConnection Connect(string path, string token, Action<ChannelEnvelope> onReceive)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://localhost{path}?access_token={Uri.EscapeDataString(token)}", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .AddJsonProtocol(o =>
            {
                foreach (var c in DocumentJson.Options.Converters)
                    o.PayloadSerializerOptions.Converters.Add(c);
            })
            .Build();
        conn.On("Receive", (ChannelEnvelope env) => onReceive(env));
        return conn;
    }

    private static ChannelEnvelope OrchestratorEnvelope(Guid tenant) => new(
        UuidV7.NewGuid(), tenant, ChannelAudience.Orchestrator, null,
        new EscalationRaised("esc-1", "rounds-exhausted", "{}", "issue-1", null),
        DateTimeOffset.UtcNow);

    [Test]
    public async Task TwoTenantIsolation_EnqueueIntoA_ZeroDeliveriesToB()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var toA = new ConcurrentBag<ChannelEnvelope>();
        var toB = new ConcurrentBag<ChannelEnvelope>();

        await using var a = Connect("/hubs/orchestrator", $"orch|{tenantA}", toA.Add);
        await using var b = Connect("/hubs/orchestrator", $"orch|{tenantB}", toB.Add);
        await a.StartAsync();
        await b.StartAsync();

        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ChannelOutboxService>();
        await svc.EnqueueAsync(OrchestratorEnvelope(tenantA));

        await Task.Delay(500);
        toB.Should().BeEmpty("a tenant-A publish must never reach a tenant-B connection (AC5 tenant grain)");
        toA.Should().ContainSingle();
    }

    [Test]
    public async Task DegradedMode_NoConsumer_RowWaitsPending_ThenDeliversOnConnect()
    {
        var tenant = Guid.NewGuid();

        // Enqueue with NO consumer connected — the row must persist pending (nothing lost).
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ChannelOutboxService>();
            await svc.EnqueueAsync(OrchestratorEnvelope(tenant));
        }
        _outbox.RowsFor(tenant).Should().ContainSingle().Which.Status.Should().Be("pending");

        // Connect — the unacked row replays on connect.
        var received = new ConcurrentBag<ChannelEnvelope>();
        await using var conn = Connect("/hubs/orchestrator", $"orch|{tenant}", received.Add);
        await conn.StartAsync();
        await Task.Delay(500);

        received.Should().ContainSingle("connect-time replay delivers the waiting row");
        _outbox.RowsFor(tenant).Single().Status.Should().Be("delivered");
    }

    [Test]
    public async Task ReplayWithoutLoss_AckRemovesFromUnacked()
    {
        var tenant = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ChannelOutboxService>();
            await svc.EnqueueAsync(OrchestratorEnvelope(tenant));
        }

        var received = new ConcurrentBag<ChannelEnvelope>();
        await using var conn = Connect("/hubs/orchestrator", $"orch|{tenant}", received.Add);
        await conn.StartAsync();
        await Task.Delay(500);

        var env = received.Should().ContainSingle().Subject;
        var ackResult = await conn.InvokeAsync<AckResult>("Ack", env.MessageId);
        ackResult.Acked.Should().BeTrue();
        // A second ack is idempotent (no double-process).
        (await conn.InvokeAsync<AckResult>("Ack", env.MessageId)).Acked.Should().BeFalse();
    }

    // ── in-memory outbox fake (deterministic; no per-tenant DB needed) ────────

    private sealed class InMemoryChannelOutbox : IChannelOutboxRepository
    {
        private readonly ConcurrentDictionary<Guid, ChannelOutboxMessage> _rows = new();

        public IReadOnlyList<ChannelOutboxMessage> RowsFor(Guid tenantId) =>
            _rows.Values.Where(r => r.TenantId == tenantId).OrderBy(r => r.Id).ToList();

        public Task<ChannelOutboxMessage> EnqueueAsync(ChannelOutboxMessage msg, CancellationToken ct = default)
        {
            msg.Status = "pending";
            _rows[msg.Id] = msg;
            return Task.FromResult(msg);
        }

        public Task<List<ChannelOutboxMessage>> ListUnackedAsync(
            Guid tenantId, string audience, Guid? recipientUserId, int limit, CancellationToken ct = default)
            => Task.FromResult(_rows.Values
                .Where(r => r.TenantId == tenantId && r.Audience == audience
                    && r.RecipientUserId == recipientUserId && r.Status != "acked")
                .OrderBy(r => r.Id).Take(limit).ToList());

        public Task MarkDeliveredAsync(Guid tenantId, Guid messageId, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(messageId, out var r) && r.Status != "acked")
            {
                r.Status = "delivered";
                r.DeliveredAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<bool> AckAsync(Guid tenantId, Guid messageId, Guid? recipientUserId, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(messageId, out var r) && r.RecipientUserId == recipientUserId && r.Status != "acked")
            {
                r.Status = "acked";
                r.AckedAt = DateTime.UtcNow;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<List<ChannelOutboxMessage>> ListStaleAsync(Guid tenantId, DateTime staleBefore, int limit, CancellationToken ct = default)
            => Task.FromResult(_rows.Values
                .Where(r => r.TenantId == tenantId && r.Status != "acked")
                .OrderBy(r => r.Id).Take(limit).ToList());

        public Task<IReadOnlyList<Guid>> ListTenantsWithPendingAsync(CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<Guid>)_rows.Values
                .Where(r => r.Status != "acked").Select(r => r.TenantId).Distinct().ToList());
    }

    private sealed class NoopEventRepository : IEventRepository
    {
        public Task<DomainEvent> AppendAsync(DomainEvent evt) => Task.FromResult(evt);
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }

    // ── test auth scheme: access_token → principal ───────────────────────────

    private sealed class TestChannelAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string Scheme = "TestChannel";

        public TestChannelAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = Request.Query["access_token"].ToString();
            if (string.IsNullOrEmpty(token))
                return Task.FromResult(AuthenticateResult.NoResult());

            var parts = token.Split('|');
            var claims = new List<Claim>();
            if (parts[0] == "orch" && parts.Length >= 2)
            {
                claims.Add(new Claim(ApprovalChannels.PrincipalTypeClaim, ApprovalChannels.OrchestratorPrincipalType));
                claims.Add(new Claim("tenantId", parts[1]));
            }
            else if (parts[0] == "user" && parts.Length >= 3)
            {
                claims.Add(new Claim("tenantId", parts[1]));
                claims.Add(new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, parts[2]));
                claims.Add(new Claim("role", "member"));
            }
            else
            {
                return Task.FromResult(AuthenticateResult.Fail("bad token"));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
        }
    }
}
