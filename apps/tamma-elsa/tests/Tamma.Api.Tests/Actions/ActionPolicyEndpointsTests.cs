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
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Core.Documents.Policy;
using Tamma.Data;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-5/43-6 — the Action Catalog policy surface against the REAL
/// (non-permissive) JWT pipeline, the <c>ScheduledTriggerEndpointsTests</c>
/// shape: a standalone Production-mode factory over the shared Postgres
/// container, minting SINGLE-claim-shape tokens (bare <c>"role"</c> only,
/// exactly as <c>JwtService</c> mints them — these pass the
/// <c>PermissionRequirement</c> gates through the fixed
/// <c>PermissionHandler.IsInRole</c> path).
///
/// <para>The host runs in SINGLE-USER mode (<c>Tamma:Mode=single-user</c>) so
/// principal writes key on the caller's user id with no tenant provisioning —
/// the RBAC gates under test (<c>ActionsManage</c>, <c>PlatformOwnerAccess</c>)
/// are mode-independent policies. SaaS principal-keying branches are covered
/// by <c>GovernancePrincipalResolverTests</c>.</para>
/// </summary>
[TestFixture]
public class ActionPolicyEndpointsTests
{
    private const string JwtSecret = "action-policy-test-secret-32-chars-xx";
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
        // SINGLE-USER mode, explicitly (a ControlPlane connection string would
        // infer SaaS; none is set, and the explicit mode wins regardless).
        Environment.SetEnvironmentVariable("Tamma__Mode", "single-user");
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
        Environment.SetEnvironmentVariable("Tamma__Mode", null);
        Environment.SetEnvironmentVariable("Cranl__EncryptionKey", null);
        Environment.SetEnvironmentVariable("TAMMA_PRESERVE_DB", null);
    }

    [SetUp]
    public async Task SetUp()
    {
        // Isolation without Respawner: only this suite's two tables matter.
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE action_assignments; TRUNCATE TABLE action_authorizations;");
    }

    private static ControlPlaneDbContext Db()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(ApiTestFixture.Postgres.GetConnectionString())
            .Options;
        return new ControlPlaneDbContext(options);
    }

    private static string MintToken(Guid userId, string role, string platformRole) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "tamma",
            audience: "tamma-api",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                // SINGLE production claim shape — bare "role" only, the shape
                // JwtService mints (MapInboundClaims=false, RoleClaimType="role").
                // These pass the ActionsManage PermissionRequirement through the
                // PermissionHandler IsInRole fix; no ClaimTypes.Role copy.
                new Claim("role", role),
                new Claim("platformRole", platformRole),
                new Claim(JwtRegisteredClaimNames.Email, "actor@example.com"),
                new Claim("name", "Actor"),
                new Claim("authMethod", "email"),
                new Claim("tenants", "[]"),
            },
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

    private HttpClient Client(Guid userId, string role, string platformRole = "user")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", MintToken(userId, role, platformRole));
        return client;
    }

    // ── RBAC: writes are admin/owner; member 403; reads any member ─────────

    [Test]
    public async Task Member_Gets403OnWrite_AdminPasses_WithBareRoleJwt()
    {
        var user = Guid.NewGuid();

        using (var member = Client(user, "member"))
        {
            var write = await member.PutAsJsonAsync(
                "/api/actions/policy/actions/tool/shell_execute/threshold",
                new { minAutonomy = AutonomyDial.AlwaysHuman });
            write.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "member must 403 at the ActionsManage policy");

            var read = await member.GetAsync("/api/actions/policy");
            read.StatusCode.Should().Be(HttpStatusCode.OK,
                "reads ride AuthenticatedAny — every role-holder needs the effective policy");
        }

        using (var admin = Client(user, "admin"))
        {
            var write = await admin.PutAsJsonAsync(
                "/api/actions/policy/actions/tool/shell_execute/threshold",
                new { minAutonomy = AutonomyDial.AlwaysHuman });
            write.StatusCode.Should().Be(HttpStatusCode.OK,
                "a production-shape bearer JWT (bare role claim) must satisfy ActionsManage");
        }
    }

    [Test]
    public async Task Unauthenticated_Gets401_OnEveryRoute()
    {
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/actions/dial")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/actions/policy")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── The platform ceiling is PlatformOwnerAccess, not ActionsManage ─────

    [Test]
    public async Task CeilingWrites_RequirePlatformAdmin_TenantAdminGets403()
    {
        var user = Guid.NewGuid();

        using (var tenantAdmin = Client(user, "admin", platformRole: "user"))
        {
            var put = await tenantAdmin.PutAsJsonAsync(
                "/api/admin/actions/ceiling/actions/effect/deploy.promote-prod/threshold",
                new { minAutonomy = AutonomyDial.AlwaysHuman });
            put.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the ceiling is the load-bearing protection — a tenant admin (even role=admin) "
                + "must never author platform-scope rows (epic README OQ4)");
        }

        using (var platformAdmin = Client(user, "member", platformRole: "platform_admin"))
        {
            var put = await platformAdmin.PutAsJsonAsync(
                "/api/admin/actions/ceiling/actions/effect/deploy.promote-prod/threshold",
                new { minAutonomy = AutonomyDial.AlwaysHuman });
            put.StatusCode.Should().Be(HttpStatusCode.OK,
                "PlatformOwnerAccess keys off the platformRole claim, not the tenant role");
        }

        await using var db = Db();
        var row = db.ActionAssignments.Single();
        row.TenantId.Should().BeNull("a ceiling row carries NEITHER principal key");
        row.UserId.Should().BeNull();
        row.TargetKey.Should().Be("effect:deploy.promote-prod");
        row.MinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);
    }

    // ── Single-field writes never reset sibling fields (the 43-0 bug class) ─

    [Test]
    public async Task PutThreshold_DoesNotResetEnforceEnabledOrRoles()
    {
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");
        const string route = "/api/actions/policy/actions/tool/file_write";

        (await admin.PutAsJsonAsync($"{route}/threshold", new { minAutonomy = 90 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync($"{route}/enforce", new { enforce = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync($"{route}/enabled", new { enabled = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync($"{route}/roles", new { allowedRoles = new[] { "developer" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Now PUT only the threshold again…
        (await admin.PutAsJsonAsync($"{route}/threshold", new { minAutonomy = 95 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = Db();
        var row = db.ActionAssignments.Single(a => a.TargetKey == "tool:file_write");
        row.MinAutonomy.Should().Be(95);
        row.Enforce.Should().BeFalse("a threshold-only PUT must not reset enforce");
        row.Enabled.Should().BeFalse("a threshold-only PUT must not re-enable");
        row.AllowedRoles.Should().BeEquivalentTo("developer");
        row.UserId.Should().Be(user, "single-user writes key on the caller's user id");
        row.TenantId.Should().BeNull();
    }

    [Test]
    public async Task MissingBodyField_Is400_NeverADefaultedWrite()
    {
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");

        var response = await admin.PutAsJsonAsync(
            "/api/actions/policy/actions/tool/file_write/threshold", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString().Should().Be("ACTION_POLICY.MISSING_FIELD");

        await using var db = Db();
        db.ActionAssignments.Count().Should().Be(0, "no row may be written");
    }

    // ── Validation: exact, rejecting, case-sensitive ────────────────────────

    [Test]
    public async Task UnknownWire_AndWrongCasing_Are400()
    {
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");

        (await admin.PutAsJsonAsync(
                "/api/actions/policy/actions/tool/not_a_tool/threshold",
                new { minAutonomy = 90 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await admin.PutAsJsonAsync(
                "/api/actions/policy/actions/tool/File_Write/threshold",
                new { minAutonomy = 90 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "wire matching is case-sensitive ordinal — bad casing is a 400, not a coercion");

        (await admin.PutAsJsonAsync(
                "/api/actions/policy/groups/not-a-group/threshold",
                new { minAutonomy = 90 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task OutOfRangeThreshold_Is400()
    {
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");

        foreach (var bad in new[] { AutonomyDial.Min - 1, AutonomyDial.AlwaysHuman + 1, 0 })
        {
            var response = await admin.PutAsJsonAsync(
                "/api/actions/policy/actions/tool/file_write/threshold",
                new { minAutonomy = bad });
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                $"{bad} is outside [{AutonomyDial.Min},{AutonomyDial.Max}] ∪ {{{AutonomyDial.AlwaysHuman}}}");
        }
    }

    [Test]
    public async Task MachineryTarget_RejectsAnyThreshold_NamingTheClassification()
    {
        // Story 43-13 AC5 — REPLACES AutomationTarget_RejectsMidRangeThreshold:
        // the old two-state rule (Min/AlwaysHuman legal on automation targets)
        // is gone, because the evaluator never resolves a machinery row through
        // the dial — a stored threshold, ANY threshold, would be a row that
        // does nothing. Red state before 43-13: Min and AlwaysHuman were 200s.
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");

        foreach (var value in new[] { AutonomyDial.Min, 85, AutonomyDial.AlwaysHuman })
        {
            var response = await admin.PutAsJsonAsync(
                "/api/actions/policy/actions/automation/channel-outbox-sweeper/threshold",
                new { minAutonomy = value });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                $"minAutonomy {value}: 'automation:channel-outbox-sweeper' is machinery "
                + "(43-11 Amendment 4) and the dial does not govern it — enabled=false is "
                + "the off-switch");
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("code").GetString()
                .Should().Be("ACTION_POLICY.MACHINERY_NOT_DIAL_GOVERNED");
        }
    }

    [Test]
    public async Task AnEngineAppendEffect_ThresholdIsAlsoRejected()
    {
        // The cleanest fail-first case in the story: effect:engine.events.append
        // is escalatable AND enforceable, so before 43-13 a mid-range write
        // SUCCEEDED — the plumbing effects were dial rows nobody meant to dial.
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");

        var response = await admin.PutAsJsonAsync(
            "/api/actions/policy/actions/effect/engine.events.append/threshold",
            new { minAutonomy = 85 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the automatic event flush is plumbing (43-11's machinery inventory) — gating "
            + "it would break the audit trail, not govern a decision");
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString()
            .Should().Be("ACTION_POLICY.MACHINERY_NOT_DIAL_GOVERNED");
    }

    [Test]
    public async Task MachineryTarget_EnabledWrite_StaysLegal()
    {
        // PUT …/enabled is untouched at every step (43-13 D7): it is the
        // admin's ONLY remaining off-switch for a machinery actor.
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");

        (await admin.PutAsJsonAsync(
                "/api/actions/policy/actions/automation/channel-outbox-sweeper/enabled",
                new { enabled = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = Db();
        db.ActionAssignments.Single(a => a.TargetKey == "automation:channel-outbox-sweeper")
            .Enabled.Should().BeFalse();
    }

    [Test]
    public async Task AMidRangeGroupWrite_IsLegal_WhenTheGroupHasDialMembers()
    {
        // Story 43-13 D7 — the group rule rewrite. platform-automation contains
        // 39 machinery rows AND four dial rows (engine.channel-outbox.enqueue,
        // schedule.create/update/delete); a group threshold is provably inert
        // for the machinery members (the evaluator never reads it), so the old
        // mid-range 400 policed nothing and is removed. Red state before
        // 43-13: this write was a 400 naming the automation members.
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");

        var response = await admin.PutAsJsonAsync(
            "/api/actions/policy/groups/platform-automation/threshold",
            new { minAutonomy = 85 });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the write legitimately governs the group's dial members");

        await using var db = Db();
        db.ActionAssignments.Single(a => a.TargetKey == "platform-automation")
            .MinAutonomy.Should().Be(85);
    }

    [Test]
    public async Task SecretReveal_ReportsMachineryNotUnenforceable()
    {
        // Story 43-13 D7 precedence pin (formerly
        // NonEnforceableTarget_SecretReveal_RejectsAThresholdWrite, which
        // asserted ACTION_POLICY.NOT_ENFORCEABLE): still a 400 — but the
        // machinery classification is the stronger, newer fact and wins the
        // error code. The evaluator's not-enforceable carve-out (epic OQ2) is
        // unchanged; only the API's refusal message moved.
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");

        var response = await admin.PutAsJsonAsync(
            "/api/actions/policy/actions/effect/secret.reveal/threshold",
            new { minAutonomy = AutonomyDial.AlwaysHuman });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString()
            .Should().Be("ACTION_POLICY.MACHINERY_NOT_DIAL_GOVERNED");
    }

    // ── Adversarial review F4 — field writes never revert the threshold ────

    [Test]
    public async Task EnforceOnlyWrite_OnAnExistingRow_PreservesItsStoredThreshold_EvenWhenTheSnapshotIsStale()
    {
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");
        const string route = "/api/actions/policy/actions/tool/file_write";

        // Seed a row through the endpoint (the local snapshot now holds 90)…
        (await admin.PutAsJsonAsync($"{route}/threshold", new { minAutonomy = 90 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // …then tighten it BEHIND the snapshot's back (the cross-pod shape:
        // pod A tightened; pod B's ≤60s-stale snapshot still says 90).
        await using (var db = Db())
        {
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE action_assignments SET "MinAutonomy" = 101 WHERE "TargetKey" = 'tool:file_write';""");
        }

        // An enforce-only write on the stale pod must NOT re-materialize the
        // stale 90 over the stored 101 (review F4: silent revert of a
        // tightening). The fix reads the row FRESH from the repository and
        // passes a null threshold, preserving the stored value.
        (await admin.PutAsJsonAsync($"{route}/enforce", new { enforce = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = Db())
        {
            var row = db.ActionAssignments.Single(a => a.TargetKey == "tool:file_write");
            row.MinAutonomy.Should().Be(101,
                "an enforce-only write must never overwrite a stored threshold "
                + "with a snapshot-derived value");
            row.Enforce.Should().BeTrue();
        }
    }

    [Test]
    public async Task EnforceFirstWrite_MaterializesAndPinsTheCurrentEffective_SoALaterGroupTighteningDoesNotReachThisAction()
    {
        // Documented materialize-and-pin semantics (43-5 story amendment,
        // 2026-07-29): a first enforce/enabled/roles write on an action with
        // no row MATERIALIZES one, pinning the threshold at the CURRENT
        // effective value. That pinned action row thereafter beats group rows
        // (?? inside the principal ladder) — a later group tightening no
        // longer reaches this member. This is the accepted design
        // consequence, not a bug; provenance ('action-override') makes the
        // pin visible in the 43-6 UI.
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");

        (await admin.PutAsJsonAsync(
                "/api/actions/policy/actions/tool/file_write/enforce", new { enforce = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = Db())
        {
            db.ActionAssignments.Single(a => a.TargetKey == "tool:file_write")
                .MinAutonomy.Should().Be(AutonomyDial.Min,
                    "with no rows the current effective is the shipped default — "
                    + "the pin is behaviour-preserving at write time");
        }

        // The later group tightening…
        (await admin.PutAsJsonAsync(
                "/api/actions/policy/groups/code-write/threshold",
                new { minAutonomy = AutonomyDial.AlwaysHuman }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // …does NOT reach the pinned member: the action row wins outright.
        var policy = await admin.GetFromJsonAsync<JsonElement>("/api/actions/policy");
        var fileWrite = policy.GetProperty("actions").EnumerateArray()
            .Single(a => a.GetProperty("key").GetString() == "tool:file_write");
        fileWrite.GetProperty("minAutonomy").GetInt32().Should().Be(AutonomyDial.Min);
        fileWrite.GetProperty("source").GetString().Should().Be("action-override",
            "provenance surfaces the pin so an admin can see why the group row lost");
    }

    // ── Group writes: machinery members are inert (43-13 D7 rewrote F5) ────

    [Test]
    public async Task GroupWrite_MidRangeOnAGroupWithMachineryMembers_IsLegal_TheyAreInert()
    {
        // REWRITTEN by Story 43-13 (formerly
        // GroupWrite_MidRangeOnAGroupWithNonEscalatableMembers_Is400NamingThem,
        // the F5 pin): the old 400 protected automation:* members from a
        // mid-range value silently behaving as Deny — but the evaluator now
        // short-circuits every machinery row BEFORE the dial comparison, so a
        // group threshold provably cannot reach them and the rejection policed
        // nothing. The secrets group's two automation members are inert; its
        // DIAL member (agent-action:audit-secrets) is what the write governs.
        var user = Guid.NewGuid();

        using (var admin = Client(user, "admin"))
        {
            (await admin.PutAsJsonAsync(
                    "/api/actions/policy/groups/secrets/threshold", new { minAutonomy = 85 }))
                .StatusCode.Should().Be(HttpStatusCode.OK,
                    "the write governs the group's dial members; the machinery members "
                    + "never consult it (43-13 AC4)");

            // The two-state values stay legal too…
            (await admin.PutAsJsonAsync(
                    "/api/actions/policy/groups/secrets/threshold",
                    new { minAutonomy = AutonomyDial.AlwaysHuman }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // …and a group with no machinery members is unchanged.
            (await admin.PutAsJsonAsync(
                    "/api/actions/policy/groups/deploy-control/threshold",
                    new { minAutonomy = 85 }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The ceiling route accepts the same write.
        using (var platformAdmin = Client(user, "member", platformRole: "platform_admin"))
        {
            (await platformAdmin.PutAsJsonAsync(
                    "/api/admin/actions/ceiling/groups/secrets/threshold",
                    new { minAutonomy = 85 }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    // ── The resolved view reflects writes, ceilings and provenance ──────────

    [Test]
    public async Task Policy_ReflectsWrites_AndTheCeilingWins_WithProvenance()
    {
        var user = Guid.NewGuid();

        using (var platformAdmin = Client(user, "member", platformRole: "platform_admin"))
        {
            (await platformAdmin.PutAsJsonAsync(
                    "/api/admin/actions/ceiling/groups/deploy-control/threshold",
                    new { minAutonomy = AutonomyDial.AlwaysHuman }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var admin = Client(user, "admin");
        // The tenant/user surface tries to lower a deploy-control member…
        (await admin.PutAsJsonAsync(
                "/api/actions/policy/actions/agent-action/deploy/threshold",
                new { minAutonomy = AutonomyDial.Min }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var policy = await admin.GetFromJsonAsync<JsonElement>("/api/actions/policy");
        var deploy = policy.GetProperty("actions").EnumerateArray()
            .Single(a => a.GetProperty("key").GetString() == "agent-action:deploy");

        deploy.GetProperty("minAutonomy").GetInt32().Should().Be(AutonomyDial.AlwaysHuman,
            "the platform ceiling composes by max() — a principal row can never lower it");
        deploy.GetProperty("source").GetString().Should().Be("platform-ceiling");

        // An un-ceilinged member still shows the principal's own row.
        var fileWrite = policy.GetProperty("actions").EnumerateArray()
            .Single(a => a.GetProperty("key").GetString() == "tool:file_write");
        fileWrite.GetProperty("source").GetString().Should().Be("system-default");

        // The dial block is published so no client hardcodes the bounds.
        policy.GetProperty("dial").GetProperty("alwaysHuman").GetInt32()
            .Should().Be(AutonomyDial.AlwaysHuman);
    }

    [Test]
    public async Task Delete_FallsBackToTheNextTier()
    {
        var user = Guid.NewGuid();
        using var admin = Client(user, "admin");
        const string route = "/api/actions/policy/actions/tool/file_write";

        (await admin.PutAsJsonAsync($"{route}/threshold", new { minAutonomy = 90 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync(route)).StatusCode.Should().Be(HttpStatusCode.OK);

        var policy = await admin.GetFromJsonAsync<JsonElement>("/api/actions/policy");
        var fileWrite = policy.GetProperty("actions").EnumerateArray()
            .Single(a => a.GetProperty("key").GetString() == "tool:file_write");
        fileWrite.GetProperty("source").GetString().Should().Be("system-default",
            "DELETE removes the row and the next tier takes over — never a zeroed value");
        fileWrite.GetProperty("minAutonomy").GetInt32().Should().Be(AutonomyDial.Min);

        (await admin.DeleteAsync(route)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Catalog_And_Dial_ArePublished_ToAnyAuthenticatedRole()
    {
        using var member = Client(Guid.NewGuid(), "member");

        var dial = await member.GetFromJsonAsync<JsonElement>("/api/actions/dial");
        dial.GetProperty("min").GetInt32().Should().Be(AutonomyDial.Min);
        dial.GetProperty("max").GetInt32().Should().Be(AutonomyDial.Max);
        dial.GetProperty("alwaysHuman").GetInt32().Should().Be(AutonomyDial.AlwaysHuman);

        var catalog = await member.GetFromJsonAsync<JsonElement>("/api/actions/catalog");
        catalog.GetArrayLength().Should().Be(Tamma.Core.Actions.ActionCatalog.All.Count,
            "the full tree-truth vocabulary is published so no client needs a local copy");
    }
}
