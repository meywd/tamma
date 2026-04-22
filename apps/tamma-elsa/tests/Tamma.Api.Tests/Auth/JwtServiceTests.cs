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

    private User MakeUser() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Email = "alice@example.com",
        DisplayName = "Alice",
        AuthMethod = "email",
        Role = "owner",
    };

    [Test]
    public void GenerateAccessToken_IncludesAllSevenRequiredClaims()
    {
        var user = MakeUser();
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
    public void GenerateAccessToken_NonOwnerGetsUserPlatformRole()
    {
        var token = _service.GenerateAccessToken(MakeUser(), Guid.NewGuid(), "member");
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
}
