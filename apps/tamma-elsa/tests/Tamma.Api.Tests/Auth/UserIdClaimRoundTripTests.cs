using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Pins the JWT-mint → JWT-validate → <see cref="ClaimsPrincipalExtensions.GetUserId"/>
/// round-trip. The Tamma.Api JwtBearer config sets <c>MapInboundClaims = false</c>,
/// which means the <c>sub</c> claim stays as <c>"sub"</c> in the principal —
/// it is NOT auto-mapped to <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
/// the way most ASP.NET Core defaults assume. Code that reads only
/// <c>NameIdentifier</c> sees null and either 401s (auth filters) or 500s
/// (handlers that dereference the parsed Guid).
///
/// This is the test that <b>would have caught</b> the systemic bug that
/// shipped in <see cref="Tamma.Api.Authorization.RequireTenantMembershipFilter"/>,
/// the prompt-store handlers, the agent-config handler, and several
/// middleware. The existing tests all built principals manually with
/// <c>new Claim(ClaimTypes.NameIdentifier, ...)</c>, bypassing the JWT
/// validation pipeline entirely. Real users go through
/// <see cref="JwtService.ValidateToken"/> after <c>JwtBearerHandler</c>
/// applies the same configured <c>TokenValidationParameters</c> as
/// production.
///
/// If a future change re-introduces a NameIdentifier-only read, run the
/// app against a real token and the production behaviour will diverge
/// from test behaviour — this fixture is the canary that says "your read
/// pattern is wrong; fix it before the deploy."
/// </summary>
[TestFixture]
public class UserIdClaimRoundTripTests
{
    private static IJwtService BuildJwtService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-key-at-least-32-bytes-for-hmac-sha256!",
                ["Jwt:Issuer"] = "tamma",
                ["Jwt:Audience"] = "tamma-api",
            })
            .Build();
        return new JwtService(config);
    }

    private static User BuildUser(Guid id) => new()
    {
        Id = id,
        Email = "alice@example.com",
        DisplayName = "alice",
        AuthMethod = "github",
        EmailVerified = true,
        Role = "member",
        PlatformRole = "user",
    };

    /// <summary>
    /// The critical assertion: after a real validate-the-JWT round-trip,
    /// <c>principal.GetUserId()</c> returns the user id. If MapInboundClaims
    /// gets flipped to true (or the helper regresses to NameIdentifier-only
    /// read), this test fails.
    /// </summary>
    [Test]
    public void GetUserId_RoundTripsThroughJwt_ForRealMintAndValidate()
    {
        var jwt = BuildJwtService();
        var userId = Guid.NewGuid();
        var token = jwt.GenerateAccessToken(BuildUser(userId), tenantId: null, role: "member");

        var principal = jwt.ValidateToken(token);

        principal.Should().NotBeNull("a freshly-minted token must validate");
        principal!.GetUserId().Should().Be(userId,
            "the user id must be reachable through the helper after a real " +
            "JwtBearer round-trip — the bug shape is reading only " +
            "ClaimTypes.NameIdentifier, which is null when MapInboundClaims=false");
    }

    /// <summary>
    /// String form of the same contract. Used by handlers that need the
    /// raw value (e.g. for log tags) before parsing.
    /// </summary>
    [Test]
    public void GetUserIdString_RoundTripsThroughJwt()
    {
        var jwt = BuildJwtService();
        var userId = Guid.NewGuid();
        var token = jwt.GenerateAccessToken(BuildUser(userId), tenantId: null, role: "member");

        var principal = jwt.ValidateToken(token);

        principal!.GetUserIdString().Should().Be(userId.ToString());
    }

    /// <summary>
    /// API-key principals set <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
    /// (no <c>sub</c>) — the helper must accept both forms or CLI/API-key
    /// callers regress.
    /// </summary>
    [Test]
    public void GetUserId_FallsBackToNameIdentifier_ForApiKeyPrincipals()
    {
        var userId = Guid.NewGuid();
        // Mirror what ApiKeyAuthHandler builds.
        var identity = new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier,
                    userId.ToString()),
            },
            authenticationType: "ApiKey");
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);

        principal.GetUserId().Should().Be(userId,
            "API-key auth sets NameIdentifier, not Sub — the helper must " +
            "accept either or CLI clients break");
    }

    /// <summary>
    /// Anonymous principals return null, not throw. Lots of middleware
    /// short-circuits on <c>GetUserId() is null</c>; throwing here would
    /// turn anonymous-allowed routes into 500s.
    /// </summary>
    [Test]
    public void GetUserId_ReturnsNull_ForAnonymousPrincipal()
    {
        var anon = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity());

        anon.GetUserId().Should().BeNull();
        anon.GetUserIdString().Should().BeNull();
    }

    /// <summary>
    /// Garbage in the sub claim returns null, not throws. Defensive against
    /// a corrupted JWT or mis-signed token reaching the helper.
    /// </summary>
    [Test]
    public void GetUserId_ReturnsNull_WhenClaimIsNotAGuid()
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim(
                    System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub,
                    "not-a-guid"),
            },
            authenticationType: "Test");
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);

        principal.GetUserId().Should().BeNull();
        principal.GetUserIdString().Should().Be("not-a-guid",
            "the string form returns whatever was in the claim; only the " +
            "Guid form parses");
    }
}
