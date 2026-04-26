using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Auth;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.RateLimit;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-R2 / Finding H2 — pin the audit-event contract added to
/// <see cref="AuthEndpoints.Logout"/> and <see cref="AuthEndpoints.SwitchOrg"/>:
/// <list type="bullet">
///   <item><description>Logout with <c>?all=true</c> emits
///     <c>USER.LOGOUT_ALL.SUCCESS</c> with <c>userId</c>, <c>actorIp</c>,
///     <c>userAgent</c>, <c>revokedTokenCount</c>, <c>jti</c>.</description></item>
///   <item><description>SwitchOrg emits <c>USER.ORG_SWITCHED.SUCCESS</c>
///     with <c>userId</c>, <c>fromTenantId</c>, <c>toTenantId</c>,
///     <c>actorIp</c>, <c>userAgent</c>.</description></item>
///   <item><description>SwitchOrg with no refresh-token-in-body adds
///     <c>reason="switch-org-no-refresh"</c> + the revoke count.</description></item>
///   <item><description>Logout-all is rate-limited per-user (3/hour).</description></item>
/// </list>
/// </summary>
[TestFixture]
public class AuthAuditEventTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private IUserRepository _userRepo = null!;
    private ITenantRepository _tenantRepo = null!;
    private ITenantMembershipRepository _membershipRepo = null!;
    private IRefreshTokenRepository _refreshTokenRepo = null!;
    private IJwtService _jwtService = null!;
    private ISessionCookieWriter _cookieWriter = null!;
    private IConfiguration _config = null!;
    private RecordingPlatformEventPublisher _publisher = null!;
    private InMemoryRateLimitService _rateLimit = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _membershipRepo = _scope.ServiceProvider.GetRequiredService<ITenantMembershipRepository>();
        _refreshTokenRepo = _scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "audit-test-secret-32-chars-minimum-x",
                ["Jwt:Issuer"] = "tamma",
                ["Jwt:Audience"] = "tamma-api",
            }).Build();
        _jwtService = new JwtService(_config);
        _cookieWriter = new SessionCookieWriter(
            _config,
            ApiTestFixture.Factory.Services.GetRequiredService<IWebHostEnvironment>());

        _publisher = new RecordingPlatformEventPublisher();
        _rateLimit = new InMemoryRateLimitService();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<User> CreateUserAsync(string email = "alice@example.com")
        => await _userRepo.CreateAsync(new User
        {
            Email = email,
            DisplayName = email.Split('@')[0],
            AuthMethod = "email",
        });

    private async Task<Tenant> CreateTenantAsync(string slugPrefix = "t")
        => await _tenantRepo.CreateAsync(new Tenant
        {
            Name = "Test " + slugPrefix,
            Slug = slugPrefix + "-" + Guid.NewGuid().ToString("N")[..8],
            Type = "org",
        });

    private static ClaimsPrincipal MakePrincipal(Guid userId, string email, string? jti = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
        };
        if (jti is not null)
            claims.Add(new Claim(JwtRegisteredClaimNames.Jti, jti));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static HttpContext MakeContext(string? userAgent = "Mozilla/5.0", string? ip = "203.0.113.7")
    {
        var ctx = new DefaultHttpContext { RequestServices = ApiTestFixture.Factory.Services };
        ctx.Response.Body = new MemoryStream();
        if (userAgent is not null)
            ctx.Request.Headers.UserAgent = userAgent;
        if (ip is not null)
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        return ctx;
    }

    private sealed class RecordingPlatformEventPublisher : IPlatformEventPublisher
    {
        public List<PlatformEvent> Events { get; } = new();
        public Task<PlatformEvent?> AppendAndPublishAsync(
            PlatformEvent evt, CancellationToken ct = default)
        {
            evt.Id = Guid.NewGuid();
            evt.CreatedAt = DateTime.UtcNow;
            Events.Add(evt);
            return Task.FromResult<PlatformEvent?>(evt);
        }
    }

    // ── Logout-all audit + rate-limit ──────────────────────────────────────

    [Test]
    public async Task LogoutAll_EmitsUserLogoutAllSuccess_WithActorAndRevokeCount()
    {
        var user = await CreateUserAsync("logout-bob@example.com");
        // Seed two refresh tokens so the revoke count is non-trivial.
        var t1Hash = HashSha256(_jwtService.GenerateRefreshToken());
        var t2Hash = HashSha256(_jwtService.GenerateRefreshToken());
        await _refreshTokenRepo.CreateAsync(user.Id, t1Hash, DateTime.UtcNow.AddDays(7));
        await _refreshTokenRepo.CreateAsync(user.Id, t2Hash, DateTime.UtcNow.AddDays(7));

        var ctx = MakeContext();
        ctx.Request.QueryString = new QueryString("?all=true");

        var result = await AuthEndpoints.Logout(
            _refreshTokenRepo, _config, _publisher, _rateLimit,
            MakePrincipal(user.Id, user.Email, jti: "jti-logout-1"), ctx);

        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        _publisher.Events.Should().ContainSingle(e => e.Type == "USER.LOGOUT_ALL.SUCCESS");
        var evt = _publisher.Events[0];

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!);
        tags!["userId"].Should().Be(user.Id.ToString("D"));
        tags["actorEmail"].Should().Be(user.Email);
        tags["jti"].Should().Be("jti-logout-1");

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data!);
        data!["userId"].GetString().Should().Be(user.Id.ToString("D"));
        data["actorEmail"].GetString().Should().Be(user.Email);
        data["userAgent"].GetString().Should().Be("Mozilla/5.0");
        data["actorIp"].GetString().Should().Be("203.0.113.7");
        data["revokedTokenCount"].GetInt32().Should().Be(2);
    }

    [Test]
    public async Task LogoutAll_RespectsXForwardedFor()
    {
        var user = await CreateUserAsync("logout-cara@example.com");
        var ctx = MakeContext(ip: "127.0.0.1");
        ctx.Request.Headers["X-Forwarded-For"] = "198.51.100.42, 10.0.0.1";
        ctx.Request.QueryString = new QueryString("?all=true");

        await AuthEndpoints.Logout(
            _refreshTokenRepo, _config, _publisher, _rateLimit,
            MakePrincipal(user.Id, user.Email), ctx);

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            _publisher.Events[0].Data!);
        data!["actorIp"].GetString().Should().Be("198.51.100.42",
            "X-Forwarded-For wins when present, with the leftmost (client) entry picked");
    }

    [Test]
    public async Task LogoutAll_WithoutAllQuery_DoesNotEmitLogoutAllEvent()
    {
        var user = await CreateUserAsync("logout-dan@example.com");
        var ctx = MakeContext();

        await AuthEndpoints.Logout(
            _refreshTokenRepo, _config, _publisher, _rateLimit,
            MakePrincipal(user.Id, user.Email), ctx);

        _publisher.Events.Should().BeEmpty();
    }

    [Test]
    public async Task LogoutAll_RateLimited_AfterThreeRequests_PerUser()
    {
        var user = await CreateUserAsync("rate-eve@example.com");
        var principal = MakePrincipal(user.Id, user.Email);

        for (var i = 0; i < 3; i++)
        {
            var ctx = MakeContext();
            ctx.Request.QueryString = new QueryString("?all=true");
            var ok = await AuthEndpoints.Logout(
                _refreshTokenRepo, _config, _publisher, _rateLimit, principal, ctx);
            await ok.ExecuteAsync(ctx);
            ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK,
                $"first three calls succeed (call #{i + 1})");
        }

        var fourthCtx = MakeContext();
        fourthCtx.Request.QueryString = new QueryString("?all=true");
        var fourth = await AuthEndpoints.Logout(
            _refreshTokenRepo, _config, _publisher, _rateLimit, principal, fourthCtx);
        await fourth.ExecuteAsync(fourthCtx);
        fourthCtx.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    // ── Switch-org audit ──────────────────────────────────────────────────

    [Test]
    public async Task SwitchOrg_EmitsUserOrgSwitchedSuccess_WithFromAndToTenants()
    {
        var user = await CreateUserAsync("switch-frank@example.com");
        var tenantA = await CreateTenantAsync("a");
        var tenantB = await CreateTenantAsync("b");
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "member");
        await _userRepo.UpdateActiveTenantAsync(user.Id, tenantA.Id);

        // Seed a refresh token so the "presented refresh token" branch runs
        // (the no-refresh path emits an extra reason tag — covered below).
        var presented = _jwtService.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(user.Id, HashSha256(presented), DateTime.UtcNow.AddDays(7));

        var ctx = MakeContext();
        await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantB.Id, RefreshToken: presented),
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher,
            MakePrincipal(user.Id, user.Email, jti: "jti-switch-1"), ctx);

        _publisher.Events.Should().ContainSingle(e => e.Type == "USER.ORG_SWITCHED.SUCCESS");
        var evt = _publisher.Events[0];
        evt.TenantId.Should().Be(tenantB.Id);

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!);
        tags!["userId"].Should().Be(user.Id.ToString("D"));
        tags["tenantId"].Should().Be(tenantB.Id.ToString("D"));
        tags["actorEmail"].Should().Be(user.Email);
        tags["jti"].Should().Be("jti-switch-1");

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data!);
        data!["fromTenantId"].GetString().Should().Be(tenantA.Id.ToString("D"));
        data["toTenantId"].GetString().Should().Be(tenantB.Id.ToString("D"));
        data["role"].GetString().Should().Be("member");
        // No-refresh path NOT taken — reason tag must be absent.
        data.ContainsKey("reason").Should().BeFalse();
    }

    [Test]
    public async Task SwitchOrg_WithoutRefreshToken_TagsReasonAsSwitchOrgNoRefresh()
    {
        var user = await CreateUserAsync("switch-grace@example.com");
        var tenantA = await CreateTenantAsync("a");
        var tenantB = await CreateTenantAsync("b");
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "member");

        // Seed two refresh tokens so the "revoke all" path actually
        // produces a non-zero count in the audit event.
        await _refreshTokenRepo.CreateAsync(user.Id,
            HashSha256(_jwtService.GenerateRefreshToken()), DateTime.UtcNow.AddDays(7));
        await _refreshTokenRepo.CreateAsync(user.Id,
            HashSha256(_jwtService.GenerateRefreshToken()), DateTime.UtcNow.AddDays(7));

        var ctx = MakeContext();
        await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantB.Id, RefreshToken: null),
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher,
            MakePrincipal(user.Id, user.Email), ctx);

        var evt = _publisher.Events.Single(e => e.Type == "USER.ORG_SWITCHED.SUCCESS");
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Data!);
        data!["reason"].GetString().Should().Be("switch-org-no-refresh");
        data["revokedTokenCount"].GetInt32().Should().Be(2);
    }

    [Test]
    public async Task SwitchOrg_NonMember_DoesNotEmitEvent()
    {
        var user = await CreateUserAsync("switch-helen@example.com");
        var ownTenant = await CreateTenantAsync("a");
        var otherTenant = await CreateTenantAsync("o");
        await _membershipRepo.AddAsync(ownTenant.Id, user.Id, "owner");
        // user is NOT a member of otherTenant

        var ctx = MakeContext();
        var result = await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(otherTenant.Id, RefreshToken: null),
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher,
            MakePrincipal(user.Id, user.Email), ctx);

        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _publisher.Events.Should().BeEmpty(
            "rejected switch-org must not pollute the audit log");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static string HashSha256(string token)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
