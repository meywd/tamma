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
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Epic41;

/// <summary>
/// Story 41-30 (AC5, D8) — the scheduled-trigger admin API against the REAL
/// (non-permissive) JWT auth pipeline, the
/// <see cref="Auth.PlatformOwnerAccessPolicyTests"/> shape: a standalone
/// Production-mode factory (⇒ SaaS mode, since ConnectionStrings:ControlPlane
/// is set) over the shared Postgres container, minting role-shaped JWTs.
///
/// <para>Pinned here: malformed cron ⇒ typed 400 with NO row written;
/// member ⇒ 403 at the ScheduleManage policy; a tenant_admin writing another
/// tenant's row ⇒ 404 (no existence leak); a non-platform-owner writing a
/// tenant_id-null TEMPLATE ⇒ 403; a definition id outside the closed
/// allowlist (<c>delete-tenant</c>) ⇒ 400.</para>
///
/// <para>Tokens are minted with the SINGLE production claim shape (bare
/// <c>"role"</c> only) — the dual-claim workaround was removed with the
/// PermissionHandler role-claim fix
/// (<c>.dev/bugs/2026-07-29-permission-handler-role-claim-mismatch.md</c>), so
/// every green assertion here is proof the real bearer-JWT pipeline passes the
/// PermissionRequirement gates for the right reason.</para>
/// </summary>
[TestFixture]
public class ScheduledTriggerEndpointsTests
{
    private const string JwtSecret = "scheduled-trigger-test-secret-32-chars-x";
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes(JwtSecret));

    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Environment.SetEnvironmentVariable("Jwt__Secret", JwtSecret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "tamma");
        Environment.SetEnvironmentVariable("Jwt__Audience", "tamma-api");
        Environment.SetEnvironmentVariable("Cranl__ApiKey", null);
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", ApiTestFixture.Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaDb", ApiTestFixture.Postgres.GetConnectionString());
        // Production + a ControlPlane connection string ⇒ TammaModeProvider
        // resolves SaaS, which is the mode whose RBAC this suite pins.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__ControlPlane", ApiTestFixture.Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "Cranl__EncryptionKey", Convert.ToBase64String(new byte[32]));
        // The factory boots Program.cs, whose Epic 19 wipe would drop and
        // re-migrate the shared container's tables mid-assembly; preserve.
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
        Environment.SetEnvironmentVariable("Jwt__Secret", null);
        Environment.SetEnvironmentVariable("Jwt__Issuer", null);
        Environment.SetEnvironmentVariable("Jwt__Audience", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__ControlPlane", null);
        Environment.SetEnvironmentVariable("Cranl__EncryptionKey", null);
        Environment.SetEnvironmentVariable("TAMMA_PRESERVE_DB", null);
    }

    [SetUp]
    public Task SetUp() => ApiTestFixture.ResetDatabaseAsync();

    private static string MintToken(string role, string platformRole, Guid tenantId) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "tamma",
            audience: "tamma-api",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim("tenantId", tenantId.ToString()),
                // SINGLE production claim shape — the bare "role" claim
                // exactly as JwtService mints it. PermissionHandler now
                // resolves roles via IsInRole (which honours the production
                // JwtBearer RoleClaimType="role"), so these tokens exercise
                // the real ScheduleManage policy pipeline end-to-end. The
                // dual-claim workaround (an extra ClaimTypes.Role copy) that
                // papered over the handler's FindFirst(ClaimTypes.Role)
                // mismatch was removed when that bug was fixed — see
                // .dev/bugs/2026-07-29-permission-handler-role-claim-mismatch.md.
                new Claim("role", role),
                new Claim("platformRole", platformRole),
                new Claim(JwtRegisteredClaimNames.Email, "actor@example.com"),
                new Claim("name", "Actor"),
                new Claim("authMethod", "email"),
                new Claim("tenants", "[]"),
            },
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

    private HttpClient Client(string role, string platformRole, Guid tenantId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", MintToken(role, platformRole, tenantId));
        return client;
    }

    private static ControlPlaneDbContext Db()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(ApiTestFixture.Postgres.GetConnectionString())
            .Options;
        return new ControlPlaneDbContext(options);
    }

    private static object ValidBody(Guid? tenantId, string name = "nightly-audit") => new
    {
        tenantId,
        definitionId = "security-audit",
        name,
        cronExpression = "0 3 * * *",
        enabled = true,
        input = new { lens = "full" },
    };

    // ── PermissionHandler role-claim fix — a production-shape bearer JWT
    // (bare "role" claim ONLY, no ClaimTypes.Role copy) passes the
    // ScheduleManage PermissionRequirement gate; member still 403s ──

    [Test]
    public async Task ProductionShapeBearerJwt_BareRoleClaimOnly_PassesPermissionRequirementGate()
    {
        var tenant = Guid.NewGuid();

        using (var admin = Client("admin", "user", tenant))
        {
            var create = await admin.PostAsJsonAsync(
                "/api/admin/scheduled-triggers/", ValidBody(tenant));
            create.StatusCode.Should().Be(HttpStatusCode.Created,
                "a real production JWT (bare \"role\" claim, MapInboundClaims=false, "
                + "RoleClaimType=\"role\") must satisfy the ScheduleManage "
                + "PermissionRequirement — this was fail-closed before the "
                + "PermissionHandler IsInRole fix");
        }

        using (var member = Client("member", "user", tenant))
        {
            var write = await member.PostAsJsonAsync(
                "/api/admin/scheduled-triggers/", ValidBody(tenant, name: "member-write"));
            write.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the fix unlocks admin/owner bearer JWTs only — member stays 403 on writes");
        }
    }

    // ── AC5 — malformed cron ⇒ typed 400, NO row written ──

    [Test]
    public async Task MalformedCron_Returns400_WithTypedError_AndWritesNoRow()
    {
        var tenant = Guid.NewGuid();
        using var client = Client("admin", "user", tenant);

        var response = await client.PostAsJsonAsync("/api/admin/scheduled-triggers/", new
        {
            tenantId = tenant,
            definitionId = "security-audit",
            name = "bad-cron",
            cronExpression = "0 3 * *", // 4 fields
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_cron_expression",
            "AC5 — malformed cron is a WRITE-time typed error, never a fire-time throw");

        await using var db = Db();
        (await db.ScheduledTriggers.CountAsync()).Should().Be(0, "no row may be written");
    }

    // ── D8 — the closed definition allowlist ──

    [Test]
    public async Task DefinitionOutsideTheAllowlist_DeleteTenant_Returns400()
    {
        var tenant = Guid.NewGuid();
        using var client = Client("admin", "user", tenant);

        var response = await client.PostAsJsonAsync("/api/admin/scheduled-triggers/", new
        {
            tenantId = tenant,
            definitionId = "delete-tenant", // the privilege-escalation case
            name = "evil",
            cronExpression = "0 3 * * *",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("definition_not_schedulable",
            "an admin-writable definition_id must stay a closed allowlist — "
            + "scheduling delete-tenant is the attack this pin guards");

        await using var db = Db();
        (await db.ScheduledTriggers.CountAsync()).Should().Be(0);
    }

    // ── D8 — member 403 on write, 200 on read ──

    [Test]
    public async Task Member_Gets403_OnWrite_And200_OnRead()
    {
        var tenant = Guid.NewGuid();

        using (var member = Client("member", "user", tenant))
        {
            var write = await member.PostAsJsonAsync(
                "/api/admin/scheduled-triggers/", ValidBody(tenant));
            write.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the ScheduleManage policy (schedules:manage → admin+owner) rejects member");

            var read = await member.GetAsync("/api/admin/scheduled-triggers/");
            read.StatusCode.Should().Be(HttpStatusCode.OK,
                "D8 — member gets 200 on read (scoped to their tenant + templates)");
        }
    }

    // ── D8 — tenant_admin can write their own tenant; another tenant 404s ──

    [Test]
    public async Task TenantAdmin_Creates_OwnTenantRow_ButAnotherTenantsRow_Is404()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        Guid createdId;
        using (var adminA = Client("admin", "user", tenantA))
        {
            var create = await adminA.PostAsJsonAsync(
                "/api/admin/scheduled-triggers/", ValidBody(tenantA));
            create.StatusCode.Should().Be(HttpStatusCode.Created);
            var body = await create.Content.ReadFromJsonAsync<JsonElement>();
            createdId = body.GetProperty("id").GetGuid();

            // Creating a row FOR another tenant is a 404 (no existence leak).
            var crossCreate = await adminA.PostAsJsonAsync(
                "/api/admin/scheduled-triggers/", ValidBody(tenantB, name: "cross"));
            crossCreate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using (var adminB = Client("admin", "user", tenantB))
        {
            // Updating tenant A's row from tenant B is a 404 too.
            var crossUpdate = await adminB.PutAsJsonAsync(
                $"/api/admin/scheduled-triggers/{createdId}",
                new { cronExpression = "0 4 * * *" });
            crossUpdate.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "another tenant's schedule must not even be visible (D8)");
        }

        await using var db = Db();
        var row = await db.ScheduledTriggers.SingleAsync();
        row.TenantId.Should().Be(tenantA);
        row.CronExpression.Should().Be("0 3 * * *", "the cross-tenant PUT must not land");
    }

    // ── D8 — a tenant_id-null TEMPLATE is platform-owner only ──

    [Test]
    public async Task NonPlatformOwner_WritingATemplateRow_Gets403_PlatformAdmin_Succeeds()
    {
        var tenant = Guid.NewGuid();

        using (var tenantOwner = Client("owner", "user", tenant))
        {
            var forbidden = await tenantOwner.PostAsJsonAsync(
                "/api/admin/scheduled-triggers/", ValidBody(tenantId: null));
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "a template materialises into EVERY tenant — tenant owners must not write one");
            var body = await forbidden.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetString().Should().Be("platform_template_forbidden");
        }

        using (var platformAdmin = Client("owner", "platform_admin", tenant))
        {
            var created = await platformAdmin.PostAsJsonAsync(
                "/api/admin/scheduled-triggers/", ValidBody(tenantId: null));
            created.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        await using var db = Db();
        var row = await db.ScheduledTriggers.SingleAsync();
        row.TenantId.Should().BeNull("the platform template row");
    }

    // ── run-now claims a synthetic manual window (D8) ──

    [Test]
    public async Task RunNow_ClaimsAManualWindow_AndRejects_Templates()
    {
        var tenant = Guid.NewGuid();
        using var admin = Client("admin", "user", tenant);

        var create = await admin.PostAsJsonAsync(
            "/api/admin/scheduled-triggers/", ValidBody(tenant));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var runNow = await admin.PostAsync(
            $"/api/admin/scheduled-triggers/{id}/run-now", content: null);
        runNow.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var claim = await runNow.Content.ReadFromJsonAsync<JsonElement>();
        claim.GetProperty("windowKey").GetString().Should().StartWith("manual:",
            "a manual run must never collide with (or suppress) a cron window");

        await using var db = Db();
        var fire = await db.ScheduledTriggerFires.SingleAsync();
        fire.TriggerId.Should().Be(id);
        fire.Outcome.Should().Be("claimed", "the engine tick drains the claim");
    }

    // ── duplicate natural key ⇒ 409 ──

    [Test]
    public async Task DuplicateSchedule_SameTenantDefinitionName_Returns409()
    {
        var tenant = Guid.NewGuid();
        using var admin = Client("admin", "user", tenant);

        (await admin.PostAsJsonAsync("/api/admin/scheduled-triggers/", ValidBody(tenant)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var duplicate = await admin.PostAsJsonAsync(
            "/api/admin/scheduled-triggers/", ValidBody(tenant));
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── run-now guard rails (2026-07-29) ──

    [Test]
    public async Task RunNow_OnADisabledTrigger_Returns409_AndClaimsNothing()
    {
        var tenant = Guid.NewGuid();
        using var admin = Client("admin", "user", tenant);

        var create = await admin.PostAsJsonAsync("/api/admin/scheduled-triggers/", new
        {
            tenantId = tenant,
            definitionId = "security-audit",
            name = "disabled-schedule",
            cronExpression = "0 3 * * *",
            enabled = false,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var runNow = await admin.PostAsync(
            $"/api/admin/scheduled-triggers/{id}/run-now", content: null);

        runNow.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "2026-07-29 contract — the engine's drain only dispatches ENABLED triggers' "
            + "claims, so accepting this claim would let it sit pending invisibly");
        var body = await runNow.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("trigger_disabled");

        await using var db = Db();
        (await db.ScheduledTriggerFires.CountAsync()).Should().Be(0,
            "no ledger claim may be written for a refused run-now");
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Two run-now requests in the SAME millisecond mint the same
    /// <c>manual:{timestamp}</c> window key; the ledger's unique
    /// <c>(TriggerId, WindowKey)</c> index rejects the second. Pre-fix that
    /// surfaced as an unhandled DbUpdateException (500); it must be a typed
    /// 409 like Create's duplicate handling. Pinned with a frozen
    /// <see cref="TimeProvider"/> so the collision is deterministic.
    /// </summary>
    [Test]
    public async Task RunNow_SameMillisecondDuplicate_Returns409_NotAnUnhandled500()
    {
        using var pinned = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<TimeProvider>(new FrozenTimeProvider(
                new DateTimeOffset(2026, 07, 29, 12, 00, 00, TimeSpan.Zero)))));
        var tenant = Guid.NewGuid();
        using var client = pinned.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", MintToken("admin", "user", tenant));

        var create = await client.PostAsJsonAsync(
            "/api/admin/scheduled-triggers/", ValidBody(tenant));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        (await client.PostAsync($"/api/admin/scheduled-triggers/{id}/run-now", content: null))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        var duplicate = await client.PostAsync(
            $"/api/admin/scheduled-triggers/{id}/run-now", content: null);

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("duplicate_run_now");

        await using var db = Db();
        (await db.ScheduledTriggerFires.CountAsync()).Should().Be(1,
            "the first claim stands; the duplicate writes nothing");
    }
}
