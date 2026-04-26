using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Auth;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-9 — direct-handler integration tests for
/// <see cref="AuthEndpoints.SwitchOrg"/>. The shared <see cref="ApiTestFixture"/>
/// boots a real Postgres container so the membership lookup, refresh-token
/// table, and user.TenantId update all exercise the same EF surface as
/// production.
///
/// Three core invariants we own here:
/// 1. Switching to a tenant the caller IS a member of returns a JWT scoped
///    to that tenant + persists it as the user's <c>active_tenant_id</c>.
/// 2. Switching to a tenant the caller is NOT a member of returns 403 and
///    does not mutate state.
/// 3. After switch-org, a refresh on the new refresh token continues to
///    issue tokens for the SAME tenant (active tenant survives refresh).
/// </summary>
[TestFixture]
public class SwitchOrgEndpointTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032 // Disposed via _scope (owned by the DI container)
    private ControlPlaneDbContext _db = null!;
    private Microsoft.Extensions.Logging.ILoggerFactory _loggerFactory = null!;
#pragma warning restore NUnit1032
    private ITenantRepository _tenantRepo = null!;
    private ITenantMembershipRepository _membershipRepo = null!;
    private IUserRepository _userRepo = null!;
    private IRefreshTokenRepository _refreshTokenRepo = null!;
    private IJwtService _jwtService = null!;
    private ISessionCookieWriter _cookieWriter = null!;
    private IConfiguration _config = null!;
    // Story 28-R2 / Finding H2 — recording publisher for SwitchOrg/Logout
    // audit events. Tests assert non-null + event content via Events list.
    private RecordingPlatformEventPublisher _publisher = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _membershipRepo = _scope.ServiceProvider.GetRequiredService<ITenantMembershipRepository>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _refreshTokenRepo = _scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();
        _loggerFactory = _scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

        // The fixture's permissive-dev branch leaves Jwt:Secret unset; mint
        // tokens here against a known secret so we control the claims and
        // the round-trip is deterministic.
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-x",
                ["Jwt:Issuer"] = "tamma",
                ["Jwt:Audience"] = "tamma-api",
            })
            .Build();
        _jwtService = new JwtService(_config);

        // Cookie writer needs a hosting environment; reuse the fixture's
        // root services so WriteSession resolves IWebHostEnvironment.
        _cookieWriter = new SessionCookieWriter(
            _config,
            ApiTestFixture.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>());

        _publisher = new RecordingPlatformEventPublisher();
    }

    /// <summary>
    /// Story 28-R2 / Finding H2 — local stub for IPlatformEventPublisher.
    /// Records every appended event so tests can assert that SwitchOrg /
    /// Logout emit the new audit events with the right shape.
    /// </summary>
    internal sealed class RecordingPlatformEventPublisher : IPlatformEventPublisher
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

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    private async Task<User> CreateUser(string email)
        => await _userRepo.CreateAsync(new User
        {
            Email = email,
            DisplayName = email.Split('@')[0],
            AuthMethod = "email",
        });

    private async Task<Tenant> CreateTenant(string slug, string name = "Test Org")
        => await _tenantRepo.CreateAsync(new Tenant
        {
            Name = name,
            Slug = slug,
            Type = "org",
        });

    private static ClaimsPrincipal Principal(Guid userId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
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

    private static async Task<int> StatusOf(IResult result)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = ApiTestFixture.Factory.Services,
        };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Test]
    public async Task SwitchOrg_Returns200_AndBindsNewTenantToSession_WhenCallerIsMember()
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
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher, Principal(user.Id), ctx);

        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        // Active tenant persisted in Settings JSON (users.TenantId is locked
        // by the prevent_tenant_id_change trigger; switch-org stashes the
        // runtime active tenant under Settings.activeTenantId instead).
        var refreshed = await _userRepo.GetByIdAsync(user.Id);
        refreshed!.TenantId.Should().Be(tenantA.Id, "personal-tenant column is immutable");

        var settings = System.Text.Json.JsonDocument.Parse(refreshed.Settings).RootElement;
        settings.TryGetProperty("activeTenantId", out var activeProp).Should().BeTrue();
        activeProp.GetString().Should().Be(tenantB.Id.ToString());

        // tamma_session cookie set.
        ctx.Response.Headers.SetCookie.ToString().Should()
            .Contain("tamma_session=", "switch-org must update the session cookie (finding 018)");
    }

    [Test]
    public async Task SwitchOrg_NewAccessToken_CarriesActiveTenantIdAndRoleForTarget()
    {
        var user = await CreateUser("bob@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        var tenantB = await CreateTenant($"b-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "member");
        await _userRepo.UpdateActiveTenantAsync(user.Id, tenantA.Id);

        var ctx = NewHttpContext();
        var result = await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantB.Id, RefreshToken: null),
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher, Principal(user.Id), ctx);

        var response = await UnwrapJson<SwitchOrgResponse>(result);
        response.TenantId.Should().Be(tenantB.Id);
        response.Role.Should().Be("member");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == "active_tenant_id"
            && c.Value == tenantB.Id.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "role" && c.Value == "member");

        // tenants[] still lists both memberships.
        var tenantsRaw = jwt.Claims.First(c => c.Type == "tenants").Value;
        var parsed = System.Text.Json.JsonDocument.Parse(tenantsRaw).RootElement;
        parsed.GetArrayLength().Should().Be(2);
    }

    // ── Negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task SwitchOrg_Returns403_WhenCallerNotMemberOfTarget()
    {
        var user = await CreateUser("eve@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        var tenantOther = await CreateTenant($"o-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _userRepo.UpdateActiveTenantAsync(user.Id, tenantA.Id);

        var ctx = NewHttpContext();
        var result = await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantOther.Id, RefreshToken: null),
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher, Principal(user.Id), ctx);

        (await StatusOf(result)).Should().Be(StatusCodes.Status403Forbidden);

        // Active tenant must not have moved.
        var refreshed = await _userRepo.GetByIdAsync(user.Id);
        refreshed!.TenantId.Should().Be(tenantA.Id);
    }

    [Test]
    public async Task SwitchOrg_Returns400_WhenTenantIdEmpty()
    {
        var user = await CreateUser("carol@example.com");
        var tenant = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenant.Id, user.Id, "owner");

        var ctx = NewHttpContext();
        var result = await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(Guid.Empty, RefreshToken: null),
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher, Principal(user.Id), ctx);
        (await StatusOf(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task SwitchOrg_Returns401_WhenPrincipalLacksSubClaim()
    {
        var anon = new ClaimsPrincipal(new ClaimsIdentity());
        var ctx = NewHttpContext();
        var result = await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(Guid.NewGuid(), RefreshToken: null),
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher, anon, ctx);
        (await StatusOf(result)).Should().Be(StatusCodes.Status401Unauthorized);
    }

    // ── Refresh-token rotation ──────────────────────────────────────────────

    [Test]
    public async Task SwitchOrg_RevokesPresentedRefreshToken_AndIssuesNewOne()
    {
        var user = await CreateUser("dave@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        var tenantB = await CreateTenant($"b-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "member");

        // Seed a refresh token for the user as if they had just logged in.
        var oldRefresh = _jwtService.GenerateRefreshToken();
        var oldRefreshHash = HashHex(oldRefresh);
        var oldToken = await _refreshTokenRepo.CreateAsync(
            user.Id, oldRefreshHash, DateTime.UtcNow.AddDays(7));

        var ctx = NewHttpContext();
        var result = await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantB.Id, RefreshToken: oldRefresh),
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher, Principal(user.Id), ctx);

        var response = await UnwrapJson<SwitchOrgResponse>(result);
        response.RefreshToken.Should().NotBe(oldRefresh,
            "switch-org rotates the refresh token alongside the access token");

        var oldRow = await _refreshTokenRepo.GetByTokenHashAsync(oldRefreshHash);
        oldRow!.RevokedAt.Should().NotBeNull("the presented refresh token must be revoked");

        var newRow = await _refreshTokenRepo.GetByTokenHashAsync(HashHex(response.RefreshToken));
        newRow.Should().NotBeNull("the new refresh token must be persisted");
        newRow!.RevokedAt.Should().BeNull();
    }

    [Test]
    public async Task SwitchOrg_RevokesAllRefreshTokens_WhenNoneInRequestBody()
    {
        var user = await CreateUser("frank@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        var tenantB = await CreateTenant($"b-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "member");

        var t1 = _jwtService.GenerateRefreshToken();
        var t2 = _jwtService.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(user.Id, HashHex(t1), DateTime.UtcNow.AddDays(7));
        await _refreshTokenRepo.CreateAsync(user.Id, HashHex(t2), DateTime.UtcNow.AddDays(7));

        var ctx = NewHttpContext();
        await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantB.Id, RefreshToken: null),
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher, Principal(user.Id), ctx);

        var r1 = await _refreshTokenRepo.GetByTokenHashAsync(HashHex(t1));
        var r2 = await _refreshTokenRepo.GetByTokenHashAsync(HashHex(t2));
        r1!.RevokedAt.Should().NotBeNull();
        r2!.RevokedAt.Should().NotBeNull();
    }

    // ── Switch + refresh preserves tenant ───────────────────────────────────

    [Test]
    public async Task RefreshAfterSwitchOrg_KeepsActiveTenant()
    {
        var user = await CreateUser("grace@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        var tenantB = await CreateTenant($"b-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "owner");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "member");
        await _userRepo.UpdateActiveTenantAsync(user.Id, tenantA.Id);

        // Switch to B.
        var ctx = NewHttpContext();
        var switchResult = await AuthEndpoints.SwitchOrg(
            new SwitchOrgRequest(tenantB.Id, RefreshToken: null),
            _userRepo, _membershipRepo, _jwtService, _refreshTokenRepo,
            _cookieWriter, _publisher, Principal(user.Id), ctx);
        var switchResp = await UnwrapJson<SwitchOrgResponse>(switchResult);

        // Refresh.
        var refreshCtx = NewHttpContext();
        var refreshResult = await AuthEndpoints.Refresh(
            new RefreshRequest(switchResp.RefreshToken),
            _refreshTokenRepo, _jwtService, _userRepo, _membershipRepo,
            _config, _loggerFactory, refreshCtx);
        var refreshResp = await UnwrapJson<RefreshResponse>(refreshResult);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(refreshResp.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == "active_tenant_id"
            && c.Value == tenantB.Id.ToString(),
            "refresh after switch-org must preserve the new active tenant");
        jwt.Claims.Should().Contain(c => c.Type == "role" && c.Value == "member",
            "refresh must re-resolve the role for the active tenant, not the old one");
    }

    [Test]
    public async Task RefreshAfterMembershipLost_FallsBackToFirstAvailableTenant()
    {
        var user = await CreateUser("henry@example.com");
        var tenantA = await CreateTenant($"a-{Guid.NewGuid():N}".Substring(0, 12));
        var tenantB = await CreateTenant($"b-{Guid.NewGuid():N}".Substring(0, 12));
        await _membershipRepo.AddAsync(tenantA.Id, user.Id, "member");
        await _membershipRepo.AddAsync(tenantB.Id, user.Id, "owner");
        await _userRepo.UpdateActiveTenantAsync(user.Id, tenantA.Id);

        // Mint a refresh token then revoke membership in A — simulates an
        // admin removing the user between the previous refresh and now.
        var refresh = _jwtService.GenerateRefreshToken();
        await _refreshTokenRepo.CreateAsync(user.Id, HashHex(refresh), DateTime.UtcNow.AddDays(7));
        await _membershipRepo.RemoveAsync(tenantA.Id, user.Id);

        var ctx = NewHttpContext();
        var result = await AuthEndpoints.Refresh(
            new RefreshRequest(refresh),
            _refreshTokenRepo, _jwtService, _userRepo, _membershipRepo,
            _config, _loggerFactory, ctx);
        var resp = await UnwrapJson<RefreshResponse>(result);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(resp.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == "active_tenant_id"
            && c.Value == tenantB.Id.ToString(),
            "lost membership in the previously-active tenant should drop to the first remaining membership");

        // Persisted in Settings JSON (users.TenantId column is immutable
        // post-bootstrap because of the prevent_tenant_id_change trigger).
        var refreshed = await _userRepo.GetByIdAsync(user.Id);
        var settings = System.Text.Json.JsonDocument.Parse(refreshed!.Settings).RootElement;
        settings.GetProperty("activeTenantId").GetString().Should().Be(tenantB.Id.ToString());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string HashHex(string token)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// Executes an <see cref="IResult"/> against a fresh in-memory context and
    /// JSON-deserializes the body to <typeparamref name="T"/>. Mirrors the
    /// helper pattern used in <see cref="Tamma.Api.Tests.Orgs.OrgEndpointHandlerTests"/>.
    /// </summary>
    private static async Task<T> UnwrapJson<T>(IResult result)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = ApiTestFixture.Factory.Services,
        };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var json = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, opts)!;
    }
}
