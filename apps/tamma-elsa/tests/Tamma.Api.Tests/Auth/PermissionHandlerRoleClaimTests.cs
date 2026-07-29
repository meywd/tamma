using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Bug fix — <c>.dev/bugs/2026-07-29-permission-handler-role-claim-mismatch.md</c>:
/// <see cref="PermissionHandler"/> used to match
/// <c>FindFirst(ClaimTypes.Role)</c>, but production JwtBearer identities carry
/// the bare <c>"role"</c> claim (<c>MapInboundClaims=false</c> +
/// <c>RoleClaimType="role"</c>), so every real bearer-JWT user fail-closed on
/// every <see cref="PermissionRequirement"/> policy. The fix resolves roles via
/// <c>IsInRole</c>, which respects each identity's own <c>RoleClaimType</c>.
///
/// <para>These tests pin BOTH claim shapes × allowed/denied roles handler-direct:
/// (a) the production bearer-JWT shape — bare <c>"role"</c> claim on an identity
/// whose <c>RoleClaimType</c> is <c>"role"</c>; (b) the default-identity shape —
/// <see cref="ClaimTypes.Role"/> on an identity with the
/// <see cref="ClaimsIdentity"/> default role claim type (the shape proxy-header
/// principals mint). They also pin that the API-key <c>permission</c>-claim path
/// and the <c>platformRole=platform_admin</c> superuser rule are unchanged.</para>
/// </summary>
[TestFixture]
public class PermissionHandlerRoleClaimTests
{
    private const string Permission = "schedules:manage"; // admin+owner reach

    // ── (a) production bearer-JWT shape: bare "role" + RoleClaimType="role" ──

    [TestCase("admin")]
    [TestCase("owner")]
    public async Task BearerJwtShape_BareRoleClaim_AllowedRole_Succeeds(string role)
    {
        var ctx = Ctx(BearerJwtPrincipal(role), Permission);

        await new PermissionHandler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue(
            "a production bearer JWT carries the bare \"role\" claim with " +
            "RoleClaimType=\"role\" — the exact shape that used to fail-close");
    }

    [Test]
    public async Task BearerJwtShape_BareRoleClaim_Member_IsDenied()
    {
        var ctx = Ctx(BearerJwtPrincipal("member"), Permission);

        await new PermissionHandler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse(
            "member lacks schedules:manage — the fix must not widen the matrix");
    }

    // ── (b) default-identity shape: ClaimTypes.Role ──

    [TestCase("admin")]
    [TestCase("owner")]
    public async Task DefaultIdentityShape_ClaimTypesRole_AllowedRole_Succeeds(string role)
    {
        var ctx = Ctx(DefaultShapePrincipal(role), Permission);

        await new PermissionHandler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue(
            "identities built with the ClaimsIdentity default RoleClaimType " +
            "(ClaimTypes.Role) must keep passing after the IsInRole fix");
    }

    [Test]
    public async Task DefaultIdentityShape_ClaimTypesRole_Member_IsDenied()
    {
        var ctx = Ctx(DefaultShapePrincipal("member"), Permission);

        await new PermissionHandler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    // ── unknown / missing role stays fail-closed ──

    [Test]
    public async Task UnknownRoleValue_IsDenied()
    {
        var ctx = Ctx(BearerJwtPrincipal("superduperadmin"), Permission);

        await new PermissionHandler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse("unknown role values fail closed");
    }

    [Test]
    public async Task NoRoleClaimAtAll_IsDenied()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()) },
            authenticationType: "Bearer",
            nameType: JwtRegisteredClaimNames.Sub,
            roleType: "role");
        var ctx = Ctx(new ClaimsPrincipal(identity), Permission);

        await new PermissionHandler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    // ── preserved paths: API-key permission claims + platform-admin superuser ──

    [TestCase(Permission)]
    [TestCase("*")]
    public async Task ApiKeyPermissionClaim_StillSucceeds(string permClaim)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim("permission", permClaim) }, authenticationType: "ApiKey");
        var ctx = Ctx(new ClaimsPrincipal(identity), Permission);

        await new PermissionHandler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue("the API-key permission-claim path is unchanged");
    }

    [Test]
    public async Task PlatformAdminSuperuserRule_StillSucceeds_WithoutAnyTenantRole()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim("platformRole", "platform_admin") },
            authenticationType: "Bearer",
            nameType: JwtRegisteredClaimNames.Sub,
            roleType: "role");
        var ctx = Ctx(new ClaimsPrincipal(identity), Permission);

        await new PermissionHandler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue(
            "Story 28-R2 / C1 — platform admins pass every PermissionRequirement " +
            "even when they are a mere member of every tenant");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    internal static AuthorizationHandlerContext Ctx(ClaimsPrincipal user, string permission)
        => new(new[] { new PermissionRequirement(permission) }, user, resource: null);

    /// <summary>The production JwtBearer shape: bare "role" claim on an
    /// identity whose RoleClaimType is "role" (Program.cs ~1471).</summary>
    internal static ClaimsPrincipal BearerJwtPrincipal(string role)
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim("role", role),
                new Claim("platformRole", "user"),
            },
            authenticationType: "Bearer",
            nameType: JwtRegisteredClaimNames.Sub,
            roleType: "role"));

    /// <summary>The default-identity shape: ClaimTypes.Role on an identity
    /// with the ClaimsIdentity default RoleClaimType (proxy-header-style).</summary>
    internal static ClaimsPrincipal DefaultShapePrincipal(string role)
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, role),
            },
            authenticationType: "Proxy"));
}

/// <summary>
/// Same bug class in <see cref="SelfOrPermissionHandler"/> — its permission
/// branch read <c>FindFirst(ClaimTypes.Role)</c> too. Pins both claim shapes
/// through the role-derived branch (no self-match: no route id / different sub).
/// </summary>
[TestFixture]
public class SelfOrPermissionHandlerRoleClaimTests
{
    private const string Permission = "users:view"; // admin+owner reach

    [TestCase("admin")]
    [TestCase("owner")]
    public async Task BearerJwtShape_BareRoleClaim_AllowedRole_Succeeds(string role)
    {
        var ctx = Ctx(PermissionHandlerRoleClaimTests.BearerJwtPrincipal(role));

        await Handler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue(
            "the SelfOrPermission role branch must honour the bare-\"role\" bearer-JWT shape");
    }

    [Test]
    public async Task BearerJwtShape_BareRoleClaim_Member_IsDenied()
    {
        var ctx = Ctx(PermissionHandlerRoleClaimTests.BearerJwtPrincipal("member"));

        await Handler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    [TestCase("admin")]
    [TestCase("owner")]
    public async Task DefaultIdentityShape_ClaimTypesRole_AllowedRole_Succeeds(string role)
    {
        var ctx = Ctx(PermissionHandlerRoleClaimTests.DefaultShapePrincipal(role));

        await Handler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Test]
    public async Task DefaultIdentityShape_ClaimTypesRole_Member_IsDenied()
    {
        var ctx = Ctx(PermissionHandlerRoleClaimTests.DefaultShapePrincipal("member"));

        await Handler().HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SelfOrPermissionHandler Handler()
    {
        // Empty route values ⇒ the self branch never matches; only the
        // role/permission branch is under test.
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        return new SelfOrPermissionHandler(accessor);
    }

    private static AuthorizationHandlerContext Ctx(ClaimsPrincipal user)
        => new(new[] { new SelfOrPermissionRequirement(Permission) }, user, resource: null);
}
