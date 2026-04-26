using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Auth;

[TestFixture]
public class JwtServiceTests
{
    private JwtService _service = null!;

    [SetUp]
    public void Setup()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-x",
                ["Jwt:Issuer"] = "tamma",
                ["Jwt:Audience"] = "tamma-api",
            })
            .Build();
        _service = new JwtService(config);
    }

    /// <summary>
    /// Default test user: a regular tenant owner (per-tenant role) but
    /// a non-platform-admin (Story 28-R2 / C1: platformRole is the
    /// dedicated <see cref="User.PlatformRole"/> column, no longer
    /// derived from <see cref="User.Role"/>).
    /// </summary>
    private User MakeUser(string platformRole = "user") => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Email = "alice@example.com",
        DisplayName = "Alice",
        AuthMethod = "email",
        Role = "owner",
        PlatformRole = platformRole,
    };

    [Test]
    public void GenerateAccessToken_IncludesAllSevenRequiredClaims()
    {
        // Story 28-R2 / C1 — explicitly construct a platform-admin user so
        // the JWT carries platformRole=platform_admin. Before C1, this test
        // relied on `role == "owner"` to imply platform_admin.
        var user = MakeUser(platformRole: "platform_admin");
        var token = _service.GenerateAccessToken(user, Guid.Parse("22222222-2222-2222-2222-222222222222"), "owner");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        jwt.Claims.Should().Contain(c => c.Type == "sub" && c.Value == user.Id.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "tenantId" && c.Value == "22222222-2222-2222-2222-222222222222");
        jwt.Claims.Should().Contain(c => c.Type == "role" && c.Value == "owner");
        jwt.Claims.Should().Contain(c => c.Type == "platformRole" && c.Value == "platform_admin");
        jwt.Claims.Should().Contain(c => c.Type == "email" && c.Value == "alice@example.com");
        jwt.Claims.Should().Contain(c => c.Type == "name" && c.Value == "Alice");
        jwt.Claims.Should().Contain(c => c.Type == "authMethod" && c.Value == "email");
    }

    [Test]
    public void GenerateAccessToken_NullTenant_EmitsEmptyTenantClaim()
    {
        var token = _service.GenerateAccessToken(MakeUser(), null, "member");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "tenantId" && c.Value == string.Empty);
    }

    [Test]
    public void GenerateAccessToken_RegularUserGetsUserPlatformRole()
    {
        // Story 28-R2 / C1 — default User.PlatformRole is "user" so a
        // regular owner-of-personal-tenant gets platformRole=user.
        var token = _service.GenerateAccessToken(MakeUser(), Guid.NewGuid(), "owner");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "platformRole" && c.Value == "user");
    }

    [Test]
    public void GenerateAccessToken_PlatformAdminUser_GetsPlatformAdminClaim()
    {
        // Story 28-R2 / C1 — explicit promotion (column flipped to
        // "platform_admin") is the ONLY way to get the elevated claim.
        var user = MakeUser(platformRole: "platform_admin");
        var token = _service.GenerateAccessToken(user, Guid.NewGuid(), "member");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "platformRole" && c.Value == "platform_admin");
    }

    [Test]
    public void GenerateAccessToken_TenantOwner_DoesNotEscalateToPlatformAdmin()
    {
        // Story 28-R2 / C1 — pin the regression. Before C1, `role: "owner"`
        // implied platformRole=platform_admin. Now the platform claim is
        // sourced exclusively from User.PlatformRole, so a tenant-owner
        // who is NOT a platform-admin must get platformRole=user.
        var user = MakeUser(platformRole: "user");
        var token = _service.GenerateAccessToken(user, Guid.NewGuid(), "owner");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "platformRole" && c.Value == "user");
    }

    [Test]
    public void GenerateAccessToken_PlatformRoleEmpty_DefaultsToUser()
    {
        // Defence-in-depth: a hand-edited DB row or pre-migration legacy
        // data should fail closed (regular user) rather than fail open.
        var user = MakeUser(platformRole: "");
        var token = _service.GenerateAccessToken(user, Guid.NewGuid(), "owner");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "platformRole" && c.Value == "user");
    }

    [Test]
    public void GenerateAccessToken_RoleClaimIsShortName()
    {
        var token = _service.GenerateAccessToken(MakeUser(), null, "owner");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "role");
        jwt.Claims.Should().NotContain(c => c.Type.Contains("schemas.microsoft.com"));
    }

    [Test]
    public void GenerateRefreshToken_Is64HexChars()
    {
        var t = _service.GenerateRefreshToken();
        t.Should().HaveLength(64);
        t.Should().MatchRegex("^[0-9a-f]+$");
    }

    [Test]
    public void ValidateToken_RoundTrip_Succeeds()
    {
        var token = _service.GenerateAccessToken(MakeUser(), Guid.NewGuid(), "owner");
        var principal = _service.ValidateToken(token);
        principal.Should().NotBeNull();
        principal!.FindFirst("role")!.Value.Should().Be("owner");
    }

    // ── Story 28-9: tenants[] + active_tenant_id ───────────────────────────

    [Test]
    public void GenerateAccessToken_EmitsActiveTenantIdClaim_MirroringTenantId()
    {
        var tenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var token = _service.GenerateAccessToken(MakeUser(), tenantId, "owner");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "active_tenant_id"
            && c.Value == tenantId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "tenantId"
            && c.Value == tenantId.ToString());
    }

    [Test]
    public void GenerateAccessToken_NullTenant_EmitsEmptyActiveTenantIdClaim()
    {
        var token = _service.GenerateAccessToken(MakeUser(), null, "member");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "active_tenant_id"
            && c.Value == string.Empty);
    }

    [Test]
    public void GenerateAccessToken_TenantsClaim_IsEmittedEvenWhenNull()
    {
        // When the caller passes no tenants list (transitional callers,
        // tests that mint a bare token), the claim must still be present as
        // an empty array so the dashboard can detect "no memberships" vs
        // "stale token".
        var token = _service.GenerateAccessToken(MakeUser(), null, "member");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var tenantsClaim = jwt.Claims.FirstOrDefault(c => c.Type == "tenants");
        tenantsClaim.Should().NotBeNull();
        tenantsClaim!.Value.Should().Be("[]");
    }

    [Test]
    public void GenerateAccessToken_TenantsClaim_SerializesEveryMembership()
    {
        var t1 = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var t2 = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
        var tenants = new[]
        {
            new TenantClaim(t1, "owner"),
            new TenantClaim(t2, "member"),
        };

        var token = _service.GenerateAccessToken(MakeUser(), t1, "owner", tenants);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var raw = jwt.Claims.First(c => c.Type == "tenants").Value;
        var parsed = System.Text.Json.JsonDocument.Parse(raw).RootElement;
        parsed.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        parsed.GetArrayLength().Should().Be(2);

        var first = parsed[0];
        first.GetProperty("tenantId").GetString().Should().Be(t1.ToString());
        first.GetProperty("role").GetString().Should().Be("owner");

        var second = parsed[1];
        second.GetProperty("tenantId").GetString().Should().Be(t2.ToString());
        second.GetProperty("role").GetString().Should().Be("member");
    }

    [Test]
    public void GenerateAccessToken_TenantsClaim_FiltersEmptyGuids()
    {
        // Defensive — a stray Guid.Empty in the membership list (corrupt
        // join, mock noise) must not produce a junk row in the JWT.
        var realTenant = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var tenants = new[]
        {
            new TenantClaim(Guid.Empty, "member"),
            new TenantClaim(realTenant, "admin"),
        };

        var token = _service.GenerateAccessToken(MakeUser(), realTenant, "admin", tenants);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var raw = jwt.Claims.First(c => c.Type == "tenants").Value;
        var parsed = System.Text.Json.JsonDocument.Parse(raw).RootElement;
        parsed.GetArrayLength().Should().Be(1);
        parsed[0].GetProperty("tenantId").GetString().Should().Be(realTenant.ToString());
    }

    [Test]
    public void ValidateToken_RoundTrip_PreservesActiveTenantAndTenantsClaims()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var tenants = new[]
        {
            new TenantClaim(t1, "owner"),
            new TenantClaim(t2, "member"),
        };
        var token = _service.GenerateAccessToken(MakeUser(), t1, "owner", tenants);

        var principal = _service.ValidateToken(token);
        principal.Should().NotBeNull();
        principal!.FindFirst("active_tenant_id")!.Value.Should().Be(t1.ToString());

        var raw = principal.FindFirst("tenants")!.Value;
        var parsed = System.Text.Json.JsonDocument.Parse(raw).RootElement;
        parsed.GetArrayLength().Should().Be(2);
    }
}
