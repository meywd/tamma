using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Auth;
using Tamma.Api.Endpoints;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-9 AC3 — refresh-token reuse-detection end-to-end against the
/// shared Postgres container. Exercises the two complementary paths the
/// audit flagged as the largest concrete gap in Epic 28:
///
/// <list type="number">
///   <item><description><b>Lineage burn</b> — present an already-revoked
///     refresh token and the entire <c>JtiChainHead</c> lineage is
///     revoked with <c>RevokedReason='reuse_detected'</c>. The next
///     refresh against any sibling in that lineage fails 401.</description></item>
///   <item><description><b>Tenant binding</b> — a refresh token issued
///     in tenant A re-issues an access token still scoped to tenant A
///     (the DB column is the binding source of truth, not the user's
///     stored active tenant).</description></item>
/// </list>
/// </summary>
[TestFixture]
public class RefreshTokenReuseDetectionTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
    private Microsoft.Extensions.Logging.ILoggerFactory _loggerFactory = null!;
#pragma warning restore NUnit1032
    private IRefreshTokenRepository _refreshTokenRepo = null!;
    private ITenantMembershipRepository _membershipRepo = null!;
    private IUserRepository _userRepo = null!;
    private ITenantRepository _tenantRepo = null!;
    private IJwtService _jwt = null!;
    private IConfiguration _config = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _refreshTokenRepo = _scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();
        _membershipRepo = _scope.ServiceProvider.GetRequiredService<ITenantMembershipRepository>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _loggerFactory = _scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-x",
                ["Jwt:Issuer"] = "tamma",
                ["Jwt:Audience"] = "tamma-api",
            })
            .Build();
        _jwt = new JwtService(_config);
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<User> CreateUser(string email) =>
        await _userRepo.CreateAsync(new User
        {
            Email = email,
            DisplayName = email.Split('@')[0],
            AuthMethod = "email",
        });

    private async Task<Tenant> CreateTenant(string slug) =>
        await _tenantRepo.CreateAsync(new Tenant
        {
            Name = "T-" + slug,
            Slug = slug,
            Type = "org",
        });

    private static string HashHex(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

    private static HttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = ApiTestFixture.Factory.Services,
        };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<int> StatusOf(IResult result)
    {
        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    private static async Task<T> UnwrapJson<T>(IResult result)
    {
        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var json = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return System.Text.Json.JsonSerializer.Deserialize<T>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    // ── AC3: tenant binding survives refresh ────────────────────────────────

    [Test]
    public async Task Refresh_PreservesBoundTenantFromRefreshTokenRow()
    {
        // Story 28-9 AC3 — the refresh token's TenantId column is the
        // binding source of truth, not the user's stored active tenant.
        var user = await CreateUser("alice@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");

        var refresh = _jwt.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(
            user.Id, tenantA.Id, HashHex(refresh),
            DateTime.UtcNow.AddDays(7), jtiChainHead: Guid.NewGuid());

        var result = await AuthEndpoints.Refresh(
            new RefreshRequest(refresh),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());

        var resp = await UnwrapJson<RefreshResponse>(result);
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(resp.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == "active_tenant_id"
            && c.Value == tenantA.Id.ToString(),
            "the refresh token's bound TenantId is the source of truth");
    }

    [Test]
    public async Task Refresh_NewRefreshTokenInheritsTenantBindingAndChainHead()
    {
        // Rotation must propagate both the tenant binding and the chain
        // head so reuse-detection sees the lineage as one unit and the
        // next refresh continues to be tenant-scoped.
        var user = await CreateUser("bob@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");

        var chainHead = Guid.NewGuid();
        var refresh = _jwt.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(
            user.Id, tenantA.Id, HashHex(refresh),
            DateTime.UtcNow.AddDays(7), chainHead);

        var result = await AuthEndpoints.Refresh(
            new RefreshRequest(refresh),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());

        var resp = await UnwrapJson<RefreshResponse>(result);
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var newRow = await db.RefreshTokens.SingleAsync(
            t => t.TokenHash == HashHex(resp.RefreshToken));
        newRow.TenantId.Should().Be(tenantA.Id, "rotation must preserve tenant binding");
        newRow.JtiChainHead.Should().Be(chainHead, "rotation must propagate chain head");
    }

    // ── AC3: reuse-detection burns the lineage ──────────────────────────────

    [Test]
    public async Task Refresh_PresentingRevokedTokenBurnsEntireChain()
    {
        // The headline AC3 scenario: rotate once (gets a new pair), then
        // present the OLD (revoked) token to /auth/refresh. The handler
        // must burn the whole lineage so both pairs are useless.
        var user = await CreateUser("carol@example.com");
        var tenant = await CreateTenant($"t-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenant.Id, user.Id, "owner");

        // Seed pair A (chain head shared across the lineage).
        var chainHead = Guid.NewGuid();
        var refreshA = _jwt.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(
            user.Id, tenant.Id, HashHex(refreshA),
            DateTime.UtcNow.AddDays(7), chainHead);

        // Rotate A → B (legitimate refresh).
        var resultB = await AuthEndpoints.Refresh(
            new RefreshRequest(refreshA),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());
        var pairB = await UnwrapJson<RefreshResponse>(resultB);

        // Replay A → reuse-detection fires.
        var replay = await AuthEndpoints.Refresh(
            new RefreshRequest(refreshA),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());
        (await StatusOf(replay)).Should().Be(StatusCodes.Status401Unauthorized);

        // Both A and B must now be revoked with `reuse_detected`.
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var rowA = await db.RefreshTokens.SingleAsync(t => t.TokenHash == HashHex(refreshA));
        var rowB = await db.RefreshTokens.SingleAsync(t => t.TokenHash == HashHex(pairB.RefreshToken));

        rowA.RevokedAt.Should().NotBeNull();
        // A was the one CONSUMED by the legitimate rotation, so it carries
        // rotation_consumed; the lineage burn only flips rows that were
        // still active at the time of replay (= the chain's tip, B).
        rowA.RevokedReason.Should().BeOneOf(
            RefreshTokenRevokedReasons.RotationConsumed,
            RefreshTokenRevokedReasons.ReuseDetected);
        rowB.RevokedAt.Should().NotBeNull(
            "the chain tip must be burned by reuse-detection");
        rowB.RevokedReason.Should().Be(RefreshTokenRevokedReasons.ReuseDetected);
    }

    [Test]
    public async Task Refresh_AfterReuseDetection_NextRefreshOnSiblingIs401()
    {
        // After the chain is burned, any subsequent attempt to refresh
        // against a sibling token must 401.
        var user = await CreateUser("dave@example.com");
        var tenant = await CreateTenant($"t-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenant.Id, user.Id, "owner");

        var chainHead = Guid.NewGuid();
        var refreshA = _jwt.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(
            user.Id, tenant.Id, HashHex(refreshA),
            DateTime.UtcNow.AddDays(7), chainHead);

        var resultB = await AuthEndpoints.Refresh(
            new RefreshRequest(refreshA),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());
        var pairB = await UnwrapJson<RefreshResponse>(resultB);

        // Replay A — burns B alongside it.
        await AuthEndpoints.Refresh(
            new RefreshRequest(refreshA),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());

        // Now try B — must 401 because B is revoked.
        var replayB = await AuthEndpoints.Refresh(
            new RefreshRequest(pairB.RefreshToken),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());
        (await StatusOf(replayB)).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task Refresh_ReuseDetection_DoesNotBurnOtherChains()
    {
        // A burned chain must not affect concurrent sessions in another
        // chain head (e.g. a second device login that started its own
        // chain).
        var user = await CreateUser("eve@example.com");
        var tenant = await CreateTenant($"t-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenant.Id, user.Id, "owner");

        // Two independent chains for the same user (two devices).
        var chain1 = Guid.NewGuid();
        var chain2 = Guid.NewGuid();
        var refresh1 = _jwt.GenerateRefreshToken();
        var refresh2 = _jwt.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(
            user.Id, tenant.Id, HashHex(refresh1),
            DateTime.UtcNow.AddDays(7), chain1);
        await _refreshTokenRepo.CreateAsync(
            user.Id, tenant.Id, HashHex(refresh2),
            DateTime.UtcNow.AddDays(7), chain2);

        // Rotate then replay chain1.
        var rot1 = await AuthEndpoints.Refresh(
            new RefreshRequest(refresh1),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());
        await UnwrapJson<RefreshResponse>(rot1);
        await AuthEndpoints.Refresh(
            new RefreshRequest(refresh1),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());

        // chain2 must still be usable — burn must be lineage-scoped.
        var rotate2 = await AuthEndpoints.Refresh(
            new RefreshRequest(refresh2),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());
        (await StatusOf(rotate2)).Should().Be(StatusCodes.Status200OK,
            "the other device's chain must survive reuse-detection on chain1");
    }

    [Test]
    public async Task Refresh_PreStoryRow_WithNullChainHead_BurnsAllUserTokens()
    {
        // Backwards compatibility — a pre-Story-28-9 row has NULL
        // JtiChainHead. When such a row is revoked and replayed, the
        // handler falls back to the previous "burn every token for the
        // user" semantics so the security posture is at least as
        // strong as before this story landed.
        var user = await CreateUser("frank@example.com");
        var tenant = await CreateTenant($"t-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenant.Id, user.Id, "owner");

        // Use the LEGACY 3-arg create so both TenantId AND JtiChainHead
        // stay NULL (simulating a row minted before this story).
        var refresh = _jwt.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(
            user.Id, HashHex(refresh), DateTime.UtcNow.AddDays(7));

        // Rotate once.
        var rot = await AuthEndpoints.Refresh(
            new RefreshRequest(refresh),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());
        var pairB = await UnwrapJson<RefreshResponse>(rot);

        // Replay the original (revoked) token — must 401.
        var replay = await AuthEndpoints.Refresh(
            new RefreshRequest(refresh),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());
        (await StatusOf(replay)).Should().Be(StatusCodes.Status401Unauthorized);

        // And the rotated successor (pairB) must also have been burned by
        // the legacy fallback (RevokeAllForUser).
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var rowB = await db.RefreshTokens.SingleAsync(t => t.TokenHash == HashHex(pairB.RefreshToken));
        rowB.RevokedAt.Should().NotBeNull();
        rowB.RevokedReason.Should().Be(RefreshTokenRevokedReasons.ReuseDetected);
    }

    // ── AC3: rotation stamps revoked reason ────────────────────────────────

    [Test]
    public async Task Refresh_NormalRotation_StampsRotationConsumed()
    {
        var user = await CreateUser("grace@example.com");
        var tenant = await CreateTenant($"t-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenant.Id, user.Id, "owner");

        var refresh = _jwt.GenerateRefreshToken();
        var seeded = await _refreshTokenRepo.CreateAsync(
            user.Id, tenant.Id, HashHex(refresh),
            DateTime.UtcNow.AddDays(7), Guid.NewGuid());

        await AuthEndpoints.Refresh(
            new RefreshRequest(refresh),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContext());

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var consumed = await db.RefreshTokens.SingleAsync(t => t.Id == seeded.Id);
        consumed.RevokedAt.Should().NotBeNull();
        consumed.RevokedReason.Should().Be(RefreshTokenRevokedReasons.RotationConsumed);
    }

    // ── AC3 follow-up: AUTH.REFRESH_REUSE_DETECTED platform_events emission ──

    [Test]
    public async Task Refresh_ReuseDetection_EmitsAuthRefreshReuseDetectedEvent()
    {
        // Story 28-9 AC3 follow-up (2026-05-30) — when a revoked refresh
        // token is replayed, an AUTH.REFRESH_REUSE_DETECTED row must land
        // in platform_events with userId + tenantId + jtiChainHead + actorIp
        // tags so SIEM / SOC2 audit can spot stolen-token replays without
        // depending on log scraping.
        var user = await CreateUser("heidi@example.com");
        var tenant = await CreateTenant($"t-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenant.Id, user.Id, "owner");

        var chainHead = Guid.NewGuid();
        var refreshA = _jwt.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(
            user.Id, tenant.Id, HashHex(refreshA),
            DateTime.UtcNow.AddDays(7), chainHead);

        // Capture emissions through a per-test publisher composed over the
        // factory DI — mirrors AuthAuditEventTests' MakeContext pattern.
        var publisher = new RecordingPlatformEventPublisher();

        // Rotate once legitimately (uses the factory's real publisher; this
        // path does not currently emit anything).
        var rotateLegit = await AuthEndpoints.Refresh(
            new RefreshRequest(refreshA),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContextWithPublisher(publisher));
        await UnwrapJson<RefreshResponse>(rotateLegit);

        // Replay the original (now-revoked) token — must emit the event.
        var replay = await AuthEndpoints.Refresh(
            new RefreshRequest(refreshA),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory,
            NewHttpContextWithPublisher(publisher, ip: "203.0.113.42"));
        (await StatusOf(replay)).Should().Be(StatusCodes.Status401Unauthorized);

        publisher.Events.Should().ContainSingle(e => e.Type == "AUTH.REFRESH_REUSE_DETECTED",
            "reuse-detection must leave an audit breadcrumb in platform_events");
        var evt = publisher.Events.Single(e => e.Type == "AUTH.REFRESH_REUSE_DETECTED");

        evt.TenantId.Should().Be(tenant.Id,
            "the platform_events row must be tenant-scoped for SIEM filtering");

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!);
        tags!["userId"].Should().Be(user.Id.ToString("D"));
        tags["tenantId"].Should().Be(tenant.Id.ToString("D"));
        tags["jtiChainHead"].Should().Be(chainHead.ToString("D"));
        tags["actorIp"].Should().Be("203.0.113.42");
        tags["source"].Should().Be("auth");

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data!);
        data!["userId"].GetString().Should().Be(user.Id.ToString("D"));
        data["jtiChainHead"].GetString().Should().Be(chainHead.ToString("D"));
        data["revokedTokenCount"].GetInt32().Should().BeGreaterThan(0,
            "the burned-chain row count goes in data so the dashboard can show it");
    }

    [Test]
    public async Task Refresh_ReuseDetection_LegacyNullChainHead_EmitsEventWithoutChainHeadTag()
    {
        // Pre-Story-28-9 row (NULL JtiChainHead, NULL TenantId) — the
        // fallback path burns every token for the user. Audit emission
        // still happens; chain-head tag is absent because there is none,
        // and the data field carries the legacy revoke count.
        var user = await CreateUser("ivan@example.com");

        var publisher = new RecordingPlatformEventPublisher();

        var refresh = _jwt.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(
            user.Id, HashHex(refresh), DateTime.UtcNow.AddDays(7));

        await AuthEndpoints.Refresh(
            new RefreshRequest(refresh),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory, NewHttpContextWithPublisher(publisher));

        await AuthEndpoints.Refresh(
            new RefreshRequest(refresh),
            _refreshTokenRepo, _jwt, _userRepo, _membershipRepo,
            _config, _loggerFactory,
            NewHttpContextWithPublisher(publisher, ip: "198.51.100.5"));

        publisher.Events.Should().ContainSingle(e => e.Type == "AUTH.REFRESH_REUSE_DETECTED");
        var evt = publisher.Events.Single(e => e.Type == "AUTH.REFRESH_REUSE_DETECTED");
        evt.TenantId.Should().BeNull(
            "legacy row had no TenantId binding, so the platform_events row is platform-scope");

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!);
        tags!["userId"].Should().Be(user.Id.ToString("D"));
        tags["actorIp"].Should().Be("198.51.100.5");
        tags.ContainsKey("jtiChainHead").Should().BeFalse(
            "no chain head means no jtiChainHead tag — the legacy fallback path");
        tags.ContainsKey("tenantId").Should().BeFalse(
            "no tenant binding means no tenantId tag in the legacy path");
    }

    private static HttpContext NewHttpContextWithPublisher(
        IPlatformEventPublisher publisher,
        string? ip = null)
    {
        // Layered DI: a per-test sub-scope registers the recording publisher
        // on top of the factory's container so the Refresh handler resolves
        // it via [FromServices] without disturbing the rest of the suite.
        var sub = new ServiceCollection();
        sub.AddSingleton(publisher);
        var subProvider = sub.BuildServiceProvider();
        var composite = new CompositeServiceProvider(subProvider, ApiTestFixture.Factory.Services);

        var ctx = new DefaultHttpContext { RequestServices = composite };
        ctx.Response.Body = new MemoryStream();
        if (ip is not null)
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        return ctx;
    }

    private sealed class CompositeServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _primary;
        private readonly IServiceProvider _fallback;
        public CompositeServiceProvider(IServiceProvider primary, IServiceProvider fallback)
        {
            _primary = primary;
            _fallback = fallback;
        }
        public object? GetService(Type serviceType)
            => _primary.GetService(serviceType) ?? _fallback.GetService(serviceType);
    }
}
