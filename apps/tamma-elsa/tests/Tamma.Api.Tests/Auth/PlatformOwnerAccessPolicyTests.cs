using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-R2 / Finding C1 — privilege-escalation regression test.
///
/// <para>Pins the contract that the new <c>PlatformOwnerAccess</c> policy
/// admits ONLY users whose JWT carries <c>platformRole=platform_admin</c>
/// (sourced from <c>users.platform_role</c>) and rejects users whose JWT
/// only carries <c>role=owner</c> (per-tenant role). Before C1, the
/// <c>OwnerAccess</c> policy that gated every <c>/api/admin/*</c> route
/// admitted any user with role-owner of any tenant — and every signed-up
/// user is auto-owner of their personal tenant, so every user passed.</para>
///
/// <para>The fixture below stands up an isolated <see cref="WebApplicationFactory{T}"/>
/// with a real (non-permissive) JWT auth pipeline so the policy
/// machinery actually runs. The stock <see cref="ApiTestFixture"/> uses
/// the permissive-dev branch where every policy is replaced with
/// <c>AllowAnonymous</c> — which is fine for handler-direct tests but
/// useless for verifying that the gate keeps non-admins out.</para>
/// </summary>
[TestFixture]
public class PlatformOwnerAccessPolicyTests
{
    private const string JwtSecret = "platform-owner-test-secret-32-chars-min-x";
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes(JwtSecret));

    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Standalone factory — does NOT share ApiTestFixture.Factory because
        // we need the real (non-permissive) JWT auth branch. Setting the env
        // vars BEFORE the factory boots is what flips Program.cs onto the
        // production auth pipeline (see the if/else in Program.cs ~line 700).
        Environment.SetEnvironmentVariable("Jwt__Secret", JwtSecret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "tamma");
        Environment.SetEnvironmentVariable("Jwt__Audience", "tamma-api");
        Environment.SetEnvironmentVariable("Cranl__ApiKey", null);
        // Reuse the shared Postgres container — the policy gate runs entirely
        // in-process so the DB doesn't matter, but Program.cs still wants a
        // valid connection string at composition time.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection",
            ApiTestFixture.Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaDb",
            ApiTestFixture.Postgres.GetConnectionString());

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.DisableAlertHostedServices();
            });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
        // Reset env so the shared ApiTestFixture (permissive-dev) keeps
        // working for sibling test classes.
        Environment.SetEnvironmentVariable("Jwt__Secret", null);
        Environment.SetEnvironmentVariable("Jwt__Issuer", null);
        Environment.SetEnvironmentVariable("Jwt__Audience", null);
    }

    /// <summary>
    /// Mints a JWT with the requested <c>role</c> + <c>platformRole</c>
    /// pair. The factory we boot above wires the production auth pipeline
    /// against this same secret, so the token round-trips cleanly into
    /// <see cref="ClaimsPrincipal"/> and through the policy gate.
    /// </summary>
    private static string MintToken(
        string role = "member",
        string platformRole = "user",
        Guid? userId = null)
    {
        var jwt = new JwtSecurityToken(
            issuer: "tamma",
            audience: "tamma-api",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub,
                    (userId ?? Guid.NewGuid()).ToString()),
                new Claim("tenantId", Guid.NewGuid().ToString()),
                new Claim("role", role),
                new Claim("platformRole", platformRole),
                new Claim(JwtRegisteredClaimNames.Email, "actor@example.com"),
                new Claim("name", "Actor"),
                new Claim("authMethod", "email"),
                new Claim("tenants", "[]"),
            },
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(
                SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private async Task<HttpResponseMessage> GetWithRoleAsync(
        string path, string role, string platformRole)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                MintToken(role: role, platformRole: platformRole));
        return await client.GetAsync(path);
    }

    // ── The platform-admin-gated routes we're pinning ──
    //
    // Curated to routes whose handlers don't depend on optional
    // services that may be missing in the bare-bones test composition root
    // (e.g. SecretQueryService, the LRU resolver). The point of this suite
    // is the *gate*, not the handler — a 403 vs. a 200/404/503/500 cleanly
    // differentiates "policy rejected" from "policy admitted; downstream
    // ran". The narrower list keeps the suite passing on a Production-mode
    // factory boot without forcing every optional service to be registered.
    private static readonly string[] PlatformAdminRoutes = new[]
    {
        "/api/admin/diagnostics/platform-queues",
        "/api/admin/kek/rotate/status",
        "/api/admin/api-keys",
        "/api/admin/analytics/summary",
        "/api/admin/tenants",
    };

    [Test]
    public async Task TenantOwner_WithoutPlatformAdminClaim_IsRejected_OnEveryAdminRoute()
    {
        // Pin C1 directly: a regular tenant-owner user must NOT pass the
        // /api/admin/* gates. Every route in PlatformAdminRoutes returns
        // 403 (Forbidden) when the JWT carries role=owner but
        // platformRole=user.
        foreach (var path in PlatformAdminRoutes)
        {
            var response = await GetWithRoleAsync(path, role: "owner", platformRole: "user");
            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                $"route {path} must reject role=owner / platformRole=user (Story 28-R2 C1)");
        }
    }

    [Test]
    public async Task PlatformAdmin_PassesPolicyGate_OnEveryAdminRoute()
    {
        // The mirror — a JWT with platformRole=platform_admin must NOT get
        // a 403. Acceptable codes: anything other than 401 (auth failed)
        // and 403 (policy rejected). 200/404/503/500 are all "policy
        // passed; downstream did its thing".
        foreach (var path in PlatformAdminRoutes)
        {
            var response = await GetWithRoleAsync(
                path, role: "member", platformRole: "platform_admin");
            response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                $"route {path} must accept platformRole=platform_admin");
            response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                $"route {path} must accept the JWT (signed with the test secret)");
        }
    }

    [Test]
    public async Task RegularMember_IsRejected()
    {
        var response = await GetWithRoleAsync(
            "/api/admin/tenants", role: "member", platformRole: "user");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task NoToken_Returns401_OnAdminRoute()
    {
        // Sanity check: missing auth header still 401s on the admin gate.
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/admin/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
