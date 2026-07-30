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
/// <para>2026-07-30 — this fixture also carries the endpoint's CONTRACT tests
/// (the "── the 2026-07-30 contract ──" region), because they need exactly the
/// same production-auth host and the env manipulation that builds it is
/// process-global. They pin the flipped default (a bare POST is a dry run), the
/// loud rejection of the old <c>dryRun=false</c> spelling, the confirmation
/// header, and the 202-plus-poll shape of an apply.</para>
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

    [Test]
    public async Task Member_Gets403_OnRunStatus()
    {
        // The status route reads sweep state and, on a miss, probes the cluster
        // lock — same platform-owner gate as the sweep itself.
        using var member = Client("member", "user");

        var response = await member.GetAsync($"/api/admin/tenants/migrate/{Guid.NewGuid():D}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── the 2026-07-30 contract ────────────────────────────────────────
    //
    // Item 1 of the sweep-hygiene follow-up: `dryRun` used to default to FALSE,
    // so a bare POST — the exact request an operator makes to see what the
    // endpoint does — applied schema migrations across the whole fleet.

    private const string MigrateBase = "/api/admin/tenants/migrate";

    private static HttpRequestMessage Post(string url, bool confirm = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (confirm)
            request.Headers.Add(
                Tamma.Api.Endpoints.Admin.AdminTenantMigrationEndpoints.ConfirmHeader,
                Tamma.Api.Endpoints.Admin.AdminTenantMigrationEndpoints.ConfirmValue);
        return request;
    }

    [Test]
    public async Task BarePost_IsADryRun_AndSaysSoUnmistakably()
    {
        using var platformAdmin = Client("member", "platform_admin");

        var response = await platformAdmin.SendAsync(Post(MigrateBase));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("mode").GetString().Should().Be("dry-run",
            "a POST with no body and no query must report, never mutate");
        body.GetProperty("applied").GetString().Should().Be("not-applied",
            "the tri-state's one guarantee-carrying value — a dry run writes nothing");
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        body.GetProperty("message").GetString().Should().Contain("DRY RUN");
    }

    [Test]
    public async Task DryRunFalse_IsRefused_LoudlyRatherThanReinterpreted()
    {
        // A caller scripted against the old default must learn about the change
        // from an error, not from a fleet that migrated when they expected a
        // report — and not from a silent no-op either.
        using var platformAdmin = Client("member", "platform_admin");

        var response = await platformAdmin.SendAsync(Post($"{MigrateBase}?dryRun=false"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("apply_requires_explicit_opt_in");
    }

    [Test]
    public async Task Apply_WithoutTheConfirmHeader_Is400()
    {
        using var platformAdmin = Client("member", "platform_admin");

        var response = await platformAdmin.SendAsync(Post($"{MigrateBase}?apply=true"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("confirmation_required",
            "the destructive routes one file over (force-delete, cleanup) demand "
            + "X-Admin-Confirm; a fleet-wide migration is not a smaller blast radius");
    }

    [Test]
    public async Task ApplyAndDryRunTogether_Is400()
    {
        using var platformAdmin = Client("member", "platform_admin");

        var response = await platformAdmin.SendAsync(
            Post($"{MigrateBase}?apply=true&dryRun=true", confirm: true));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("conflicting_mode");
    }

    [Test]
    public async Task Apply_WithConfirmHeader_Is202_AndTheRunIsPollableToCompletion()
    {
        // Item 3: the sweep used to run inside the request, so a large fleet
        // timed the caller out while the DDL kept going. Now: prompt 202 + a
        // run id. (The fleet here is empty — [SetUp] truncated tenants — so the
        // background run completes almost immediately.)
        using var platformAdmin = Client("member", "platform_admin");

        var response = await platformAdmin.SendAsync(
            Post($"{MigrateBase}?apply=true", confirm: true));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        accepted.GetProperty("mode").GetString().Should().Be("apply");
        accepted.GetProperty("applied").GetString().Should().Be("partially-applied",
            "the run is accepted and may already be issuing DDL by the time the caller reads "
            + "this; only 'not-applied' is allowed to be a guarantee (2026-07-30 Finding 1.3)");
        var runId = accepted.GetProperty("runId").GetGuid();
        var statusUrl = accepted.GetProperty("statusUrl").GetString()!;
        statusUrl.Should().Be($"/api/admin/tenants/migrate/{runId:D}");

        var final = await PollToTerminalAsync(platformAdmin, statusUrl);
        final.GetProperty("state").GetString().Should().Be("completed");
        final.GetProperty("mode").GetString().Should().Be("apply");
        final.GetProperty("result").GetProperty("total").GetInt32().Should().Be(0);
    }

    [Test]
    public async Task DryRun_CanOptIntoTheSame202_ForAVeryLargeFleet()
    {
        using var platformAdmin = Client("member", "platform_admin");

        var response = await platformAdmin.SendAsync(Post($"{MigrateBase}?async=true"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        accepted.GetProperty("mode").GetString().Should().Be("dry-run");
        accepted.GetProperty("applied").GetString().Should().Be("not-applied");

        var final = await PollToTerminalAsync(
            platformAdmin, accepted.GetProperty("statusUrl").GetString()!);
        final.GetProperty("state").GetString().Should().Be("completed");
        final.GetProperty("result").GetProperty("dryRun").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task UnknownRunId_Is404_AndSaysRunStateIsPerInstance()
    {
        using var platformAdmin = Client("member", "platform_admin");

        var response = await platformAdmin.GetAsync(
            $"/api/admin/tenants/migrate/{Guid.NewGuid():D}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("run_not_found_on_this_instance");
        body.GetProperty("sweepRunningOnSomeInstance").GetBoolean().Should().BeFalse();
    }

    private static async Task<JsonElement> PollToTerminalAsync(HttpClient client, string statusUrl)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetAsync(statusUrl);
            status.StatusCode.Should().Be(HttpStatusCode.OK,
                "the run was started by this very instance, so its status must be readable");
            var body = await status.Content.ReadFromJsonAsync<JsonElement>();
            if (body.GetProperty("state").GetString() != "running") return body;
            await Task.Delay(50);
        }

        throw new TimeoutException($"{statusUrl} never left the running state");
    }
}
