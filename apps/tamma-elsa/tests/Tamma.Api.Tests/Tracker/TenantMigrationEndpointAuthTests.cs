using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Tamma.Api.Tests.Infrastructure;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-1 AC8 — the HTTP-level authorization proof for
/// <c>POST /api/admin/tenants/migrate</c>, the fleet-wide tenant-DDL sweep.
///
/// <para><see cref="TenantMigrationSweeperTests"/> pins the sweeper's BEHAVIOUR
/// (it drives <c>TenantMigrationSweeper</c> directly against its own Postgres
/// container). Nothing exercised the ROUTE's
/// <c>RequireAuthorization("PlatformOwnerAccess")</c> gate over HTTP, so the
/// "platform-owner only" guarantee on the platform's most destructive
/// cross-tenant primitive was inspected-only. This suite closes that: the gate
/// runs for real, through the real (non-permissive) JWT pipeline.</para>
///
/// <para>Fixture shape copied from
/// <c>Tamma.Api.Tests.Epic41.ScheduledTriggerEndpointsTests</c>: a standalone
/// Production-mode <see cref="WebApplicationFactory{T}"/> over the shared
/// <see cref="ApiTestFixture.Postgres"/> container, with
/// <c>ConnectionStrings:ControlPlane</c> set ⇒ SaaS mode (the mode a
/// fleet-wide sweep exists for). Tokens carry the SINGLE production claim
/// shape — a bare <c>"role"</c> claim exactly as <c>JwtService</c> mints it
/// (<c>MapInboundClaims=false</c>, <c>RoleClaimType="role"</c>) — never the
/// retired dual-claim (<c>ClaimTypes.Role</c> copy) workaround, so every
/// assertion here is proof about the real bearer-JWT pipeline.</para>
///
/// <para>The authorized call passes <c>dryRun=true</c>: the route's handler is
/// then side-effect-free (the sweeper reports pending counts and applies
/// nothing), and the shared container's <c>tenants</c> table is truncated by
/// <see cref="ApiTestFixture.ResetDatabaseAsync"/> in <c>[SetUp]</c>, so the
/// sweep enumerates an empty fleet and returns immediately.</para>
///
/// <para>REQUIRES DOCKER (the shared assembly fixture's Postgres container).</para>
/// </summary>
[TestFixture]
[Category("Docker")]
public class TenantMigrationEndpointAuthTests
{
    private const string JwtSecret = "tenant-migrate-auth-test-secret-32-chars";
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes(JwtSecret));

    /// <summary>The route under test. dryRun keeps the authorized call inert.</summary>
    private const string MigrateRoute = "/api/admin/tenants/migrate?dryRun=true";

    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Setting these BEFORE the factory boots is what flips Program.cs onto
        // the production auth pipeline (the permissive-dev branch replaces
        // every policy with AllowAnonymous, which would make this suite
        // vacuous).
        Environment.SetEnvironmentVariable("Jwt__Secret", JwtSecret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "tamma");
        Environment.SetEnvironmentVariable("Jwt__Audience", "tamma-api");
        Environment.SetEnvironmentVariable("Cranl__ApiKey", null);
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", ApiTestFixture.Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaDb", ApiTestFixture.Postgres.GetConnectionString());
        // Production + a ControlPlane connection string ⇒ SaaS mode.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__ControlPlane", ApiTestFixture.Postgres.GetConnectionString());
        // Production hard-requires an encryption key (the Phase-2 startup
        // seeder encrypts the central pool row's admin connection string).
        // Any base64 32-byte value works — nothing under test decrypts it.
        Environment.SetEnvironmentVariable(
            "Cranl__EncryptionKey", Convert.ToBase64String(new byte[32]));
        // Program.cs's Epic 19 wipe would drop and re-migrate the shared
        // container's tables mid-assembly; preserve.
        Environment.SetEnvironmentVariable("TAMMA_PRESERVE_DB", "1");

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
        // Reset the process-global env so the shared (permissive-dev)
        // ApiTestFixture keeps working for sibling suites.
        Environment.SetEnvironmentVariable("Jwt__Secret", null);
        Environment.SetEnvironmentVariable("Jwt__Issuer", null);
        Environment.SetEnvironmentVariable("Jwt__Audience", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__ControlPlane", null);
        Environment.SetEnvironmentVariable("Cranl__EncryptionKey", null);
        Environment.SetEnvironmentVariable("TAMMA_PRESERVE_DB", null);
    }

    [SetUp]
    public Task SetUp() => ApiTestFixture.ResetDatabaseAsync();

    private static string MintToken(string role, string platformRole) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "tamma",
            audience: "tamma-api",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim("tenantId", Guid.NewGuid().ToString()),
                // SINGLE production claim shape — bare "role" only.
                new Claim("role", role),
                new Claim("platformRole", platformRole),
                new Claim(JwtRegisteredClaimNames.Email, "actor@example.com"),
                new Claim("name", "Actor"),
                new Claim("authMethod", "email"),
                new Claim("tenants", "[]"),
            },
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

    private HttpClient Client(string role, string platformRole)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", MintToken(role, platformRole));
        return client;
    }

    // ── Denials ──────────────────────────────────────────────────────────

    [Test]
    public async Task Member_Gets403_OnMigrateSweep()
    {
        using var member = Client("member", "user");

        var response = await member.PostAsync(MigrateRoute, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a tenant member must never reach the fleet-wide tenant-DDL sweep");
    }

    [TestCase("admin")]
    [TestCase("owner")]
    public async Task TenantAdminOrOwner_WithoutPlatformRole_Gets403_OnMigrateSweep(string role)
    {
        // Finding C1 in the shape that matters for this route: every signed-up
        // user is auto-owner of their personal tenant, so a per-tenant
        // admin/owner role must NOT clear a PLATFORM gate whose blast radius
        // is every tenant schema on the fleet.
        using var tenantAdmin = Client(role, "user");

        var response = await tenantAdmin.PostAsync(MigrateRoute, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"role={role} / platformRole=user must not pass PlatformOwnerAccess");
    }

    [Test]
    public async Task Unauthenticated_Gets401_OnMigrateSweep()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsync(MigrateRoute, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "no bearer token ⇒ the admin gate 401s before any handler runs");
    }

    // ── The mirror: a platform admin clears the gate ─────────────────────

    [Test]
    public async Task PlatformAdmin_ClearsTheGate_AndDryRunSweepSucceeds()
    {
        // role=member deliberately: PermissionHandler grants platform_admin
        // every PermissionRequirement, so the composed (group: AdminAccess) +
        // (route: PlatformOwnerAccess) pair resolves to "yes" without the
        // operator also holding a per-tenant admin/owner role.
        using var platformAdmin = Client("member", "platform_admin");

        var response = await platformAdmin.PostAsync(MigrateRoute, content: null);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "platformRole=platform_admin must clear PlatformOwnerAccess");
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "the token is signed with this suite's secret");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the route returns Results.Ok(sweepResult) for an authorized call");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue(
            "dryRun=true rode the query string into the sweeper — the call applied nothing");
        body.GetProperty("total").GetInt32().Should().Be(0,
            "[SetUp] truncated the tenants table, so the sweep enumerates an empty fleet");
        body.GetProperty("failed").GetInt32().Should().Be(0);
    }
}
