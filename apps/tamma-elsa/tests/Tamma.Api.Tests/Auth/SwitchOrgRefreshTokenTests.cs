using System.Security.Claims;
using System.Security.Cryptography;
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
/// Story 28-9 AC3 — switch-org-specific refresh-token semantics. Two
/// invariants:
///
/// <list type="number">
///   <item><description>The new refresh token MUST be bound to the
///     TARGET tenant (the one we switched to), not the source tenant.
///     This is the database side of the cross-tenant refresh leak
///     protection.</description></item>
///   <item><description>Switch-org STARTS A NEW CHAIN — the refresh
///     lineage from the source tenant terminates at the old refresh
///     row; the new chain head identifies the post-switch session.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class SwitchOrgRefreshTokenTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private IRefreshTokenRepository _refreshTokenRepo = null!;
    private ITenantMembershipRepository _membershipRepo = null!;
    private IUserRepository _userRepo = null!;
    private ITenantRepository _tenantRepo = null!;
    private IJwtService _jwt = null!;
    private ISessionCookieWriter _cookieWriter = null!;
    private RecordingPlatformEventPublisher _publisher = null!;

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

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-x",
                ["Jwt:Issuer"] = "tamma",
                ["Jwt:Audience"] = "tamma-api",
            })
            .Build();
        _jwt = new JwtService(config);
        _cookieWriter = new SessionCookieWriter(
            config,
            ApiTestFixture.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>());
        _publisher = new RecordingPlatformEventPublisher();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

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

    private static ClaimsPrincipal Principal(Guid userId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static HttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = ApiTestFixture.Factory.Services,
        };
        ctx.Response.Body = new MemoryStream();
        return ctx;
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

    private static string HashHex(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

    // ── AC3: new refresh row is bound to target tenant ──────────────────────

    [Test]
    public async Task SwitchOrg_NewRefreshToken_IsBoundToTargetTenant()
    {
        var user = await CreateUser("alice@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        var tenantB = await CreateTenant($"b-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "member");
        await _userRepo.UpdateActiveTenantAsync(user.Id, tenantA.Id);

        var ctx = NewHttpContext();
        var result = await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantB.Id, RefreshToken: null),
            _userRepo, _membershipRepo, _jwt, _refreshTokenRepo,
            _cookieWriter, _publisher, _db, Principal(user.Id), ctx);
        var resp = await UnwrapJson<SwitchOrgResponse>(result);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var row = await db.RefreshTokens.SingleAsync(t => t.TokenHash == HashHex(resp.RefreshToken));
        row.TenantId.Should().Be(tenantB.Id,
            "AC3 — the new refresh token must be bound to the TARGET tenant, not the source");
    }

    [Test]
    public async Task SwitchOrg_NewRefreshToken_StartsNewChain()
    {
        // AC3 — switch-org TERMINATES the source-tenant lineage and
        // starts a new chain head for the target-tenant session. The
        // refresh token from the source tenant can no longer be used
        // (it's revoked with `switch_org`).
        var user = await CreateUser("bob@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        var tenantB = await CreateTenant($"b-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "member");

        var oldChain = Guid.NewGuid();
        var oldRefresh = _jwt.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(
            user.Id, tenantA.Id, HashHex(oldRefresh),
            DateTime.UtcNow.AddDays(7), oldChain);

        var ctx = NewHttpContext();
        var result = await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantB.Id, RefreshToken: oldRefresh),
            _userRepo, _membershipRepo, _jwt, _refreshTokenRepo,
            _cookieWriter, _publisher, _db, Principal(user.Id), ctx);
        var resp = await UnwrapJson<SwitchOrgResponse>(result);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var newRow = await db.RefreshTokens.SingleAsync(t => t.TokenHash == HashHex(resp.RefreshToken));
        newRow.JtiChainHead.Should().NotBeNull();
        newRow.JtiChainHead!.Value.Should().NotBe(oldChain,
            "switch-org must start a NEW chain, not extend the source-tenant lineage");
    }

    // ── AC3: source refresh token stamped with switch_org reason ────────────

    [Test]
    public async Task SwitchOrg_RevokesPresentedToken_WithSwitchOrgReason()
    {
        var user = await CreateUser("carol@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        var tenantB = await CreateTenant($"b-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "member");

        var oldRefresh = _jwt.GenerateRefreshToken();
        var oldToken = await _refreshTokenRepo.CreateAsync(
            user.Id, tenantA.Id, HashHex(oldRefresh),
            DateTime.UtcNow.AddDays(7), Guid.NewGuid());

        var ctx = NewHttpContext();
        await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantB.Id, RefreshToken: oldRefresh),
            _userRepo, _membershipRepo, _jwt, _refreshTokenRepo,
            _cookieWriter, _publisher, _db, Principal(user.Id), ctx);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var revoked = await db.RefreshTokens.SingleAsync(t => t.Id == oldToken.Id);
        revoked.RevokedAt.Should().NotBeNull();
        revoked.RevokedReason.Should().Be(RefreshTokenRevokedReasons.SwitchOrg);
    }

    [Test]
    public async Task SwitchOrg_NoRefreshTokenInBody_BulkRevokeUsesSwitchOrgReason()
    {
        // When the caller doesn't present a refresh token, switch-org
        // burns every active refresh token for the user — but the reason
        // column must distinguish this from a generic logout-all so SIEM
        // can spot the switch-org-no-refresh pattern (legacy clients,
        // dashboard tab with no refresh token state).
        var user = await CreateUser("dave@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        var tenantB = await CreateTenant($"b-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "member");

        var t1 = _jwt.GenerateRefreshToken();
        var t2 = _jwt.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(
            user.Id, tenantA.Id, HashHex(t1), DateTime.UtcNow.AddDays(7), Guid.NewGuid());
        await _refreshTokenRepo.CreateAsync(
            user.Id, tenantA.Id, HashHex(t2), DateTime.UtcNow.AddDays(7), Guid.NewGuid());

        var ctx = NewHttpContext();
        await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantB.Id, RefreshToken: null),
            _userRepo, _membershipRepo, _jwt, _refreshTokenRepo,
            _cookieWriter, _publisher, _db, Principal(user.Id), ctx);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var revoked = await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.TenantId == tenantA.Id)
            .ToListAsync();
        revoked.Should().HaveCount(2);
        revoked.Should().AllSatisfy(r =>
        {
            r.RevokedAt.Should().NotBeNull();
            r.RevokedReason.Should().Be(RefreshTokenRevokedReasons.SwitchOrg);
        });
    }
}
