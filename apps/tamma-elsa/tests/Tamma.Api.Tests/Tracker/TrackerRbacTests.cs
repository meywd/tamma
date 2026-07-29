using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-2 AC4 — the tracker's RBAC over the REAL (non-permissive) JWT
/// pipeline, plus the route-table invariants (AC2 ordering and AC10's
/// bidirectional catalog check).
///
/// <para>Fixture shape copied from <c>Actions/ActionPolicyEndpointsTests</c>
/// and <c>Tracker/TenantMigrationEndpointAuthTests</c>: a standalone
/// Production-mode factory over the shared Postgres container, minting
/// PRODUCTION-SHAPE tokens — a bare <c>"role"</c> claim only, exactly as
/// <c>JwtService</c> mints them (<c>MapInboundClaims=false</c>,
/// <c>RoleClaimType="role"</c>). The retired dual-claim
/// (<c>ClaimTypes.Role</c> copy) workaround is deliberately NOT used, so every
/// assertion here is proof about the real bearer-JWT pipeline
/// (<c>.dev/bugs/2026-07-29-permission-handler-role-claim-mismatch.md</c>).</para>
///
/// <para>The POSITIVE assertions are <c>NotBe(Forbidden)</c>, not
/// <c>Be(OK)</c>: what is under test is the POLICY, and a handler reaching a
/// 400/404/409 has already proved the gate admitted the caller. Asserting a
/// 2xx would additionally require a provisioned tenant schema, which is
/// <c>TrackerEndpointsTests</c>'s job.</para>
///
/// <para>REQUIRES DOCKER (the shared assembly fixture's Postgres container).</para>
/// </summary>
[TestFixture]
[Category("Docker")]
public class TrackerRbacTests
{
    private const string JwtSecret = "tracker-rbac-test-secret-32-characters";
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
        Environment.SetEnvironmentVariable("Tamma__Mode", "single-user");
        Environment.SetEnvironmentVariable(
            "Cranl__EncryptionKey", Convert.ToBase64String(new byte[32]));
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

    private static string MintToken(string role) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "tamma",
            audience: "tamma-api",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                // SINGLE production claim shape — bare "role" only.
                new Claim("role", role),
                new Claim("platformRole", "user"),
                new Claim(JwtRegisteredClaimNames.Email, "actor@example.com"),
                new Claim("name", "Actor"),
                new Claim("authMethod", "email"),
                new Claim("tenants", "[]"),
            },
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

    private HttpClient Client(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken(role));
        return client;
    }

    // ══════════════════ tracker:view — a member may work ════════════════════

    [Test]
    public async Task Member_may_create_and_move_a_work_item()
    {
        using var member = Client("member");

        var create = await member.PostAsJsonAsync("/api/work-items", new
        {
            projectId = Guid.NewGuid(),
            title = "member-filed bug",
            kind = "task",
        });
        create.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "a tracker in which a member cannot FILE work is not a tracker (AC4)");

        var move = await member.PostAsJsonAsync(
            $"/api/work-items/{Guid.NewGuid()}/status", new { status = "in_progress" });
        move.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "…nor one in which a member cannot MOVE their own card");

        var assign = await member.PostAsJsonAsync(
            $"/api/work-items/{Guid.NewGuid()}/assign", new { assigneeUserId = (Guid?)null });
        assign.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

        var read = await member.GetAsync("/api/work-items");
        read.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "reads ride tracker:view too");
    }

    // ══════════════════ tracker:manage — structure is admin+ ════════════════

    [Test]
    public async Task Member_may_not_create_or_delete_a_project()
    {
        using var member = Client("member");

        (await member.PostAsJsonAsync("/api/projects", new { key = "TAM", name = "p" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "a project key is everyone's identifier namespace — structure is admin+ (AC4)");

        (await member.DeleteAsync($"/api/projects/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await member.PatchAsJsonAsync($"/api/projects/{Guid.NewGuid()}", new { name = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The tenant-wide preference row is structure too (there is no per-user
        // preference plane in SaaS — a member editing it changes everyone's
        // defaults).
        (await member.PutAsJsonAsync("/api/tracker/preferences", new { defaultKind = "task" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // …but a member can still READ the resolved preferences.
        (await member.GetAsync("/api/tracker/preferences"))
            .StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [TestCase("admin")]
    [TestCase("owner")]
    public async Task Tenant_admin_is_not_403d(string role)
    {
        // THE SettingsManage TRAP, pinned: settings:manage is ["owner"] only, so
        // reusing it (as several specs casually suggest) would 403 every
        // tenant_admin on a surface they must administer. TrackerManage maps to
        // tracker:manage = ["admin","owner"] precisely to avoid that.
        using var client = Client(role);

        (await client.PostAsJsonAsync("/api/projects", new { key = "TAM", name = "p" }))
            .StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                $"role={role} must clear TrackerManage");

        (await client.PutAsJsonAsync("/api/tracker/preferences", new { defaultKind = "task" }))
            .StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Unauthenticated_is_401_on_every_tracker_route()
    {
        using var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync("/api/work-items")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/work-items", new { title = "x" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/projects")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    // ══════════════════ Three-place RBAC lockstep ═══════════════════════════

    [Test]
    public void Policy_names_are_in_the_roster()
    {
        // Place 1 — the permission matrix.
        Permissions.Matrix.Should().ContainKey("tracker:view");
        Permissions.Matrix.Should().ContainKey("tracker:manage");
        Permissions.Matrix["tracker:view"].Should().BeEquivalentTo(["member", "admin", "owner"]);
        Permissions.Matrix["tracker:manage"].Should().BeEquivalentTo(["admin", "owner"]);
        Permissions.HasPermission("member", "tracker:view").Should().BeTrue();
        Permissions.HasPermission("member", "tracker:manage").Should().BeFalse();
        Permissions.HasPermission("admin", "tracker:manage").Should().BeTrue();

        // Places 2 AND 3 — the AddAuthorization block and the Development
        // permissive roster array. A policy missing from EITHER is unresolvable
        // here; the roster array is the one that gets forgotten, which is why
        // this assertion exists at all (plan test 6).
        var policies = _factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
        policies.GetPolicyAsync("TrackerView").GetAwaiter().GetResult()
            .Should().NotBeNull("TrackerView must be registered in the AddAuthorization block");
        policies.GetPolicyAsync("TrackerManage").GetAwaiter().GetResult()
            .Should().NotBeNull("TrackerManage must be registered in the AddAuthorization block");
    }

    [Test]
    public void Tracker_manage_does_not_reuse_settings_manage()
    {
        // settings:manage is owner-only. If TrackerManage ever gets pointed at
        // it, this fails BEFORE a tenant_admin discovers it in production.
        Permissions.Matrix["settings:manage"].Should().BeEquivalentTo(["owner"]);
        Permissions.Matrix["tracker:manage"].Should().Contain("admin",
            "the whole reason a dedicated tracker:manage exists (Program.cs:1615-1617's rationale)");
    }

    // ══════════════════ AC2 — route ordering ════════════════════════════════

    [Test]
    public void Literals_precede_parameterized()
    {
        var routes = TrackerRoutes();

        var assignable = IndexOf(routes, "GET", "/api/work-items/assignable");
        var byKey = IndexOf(routes, "GET", "/api/work-items/by-key/{key}");
        var byId = IndexOf(routes, "GET", "/api/work-items/{id:guid}");

        assignable.Should().BeGreaterThanOrEqualTo(0, "/work-items/assignable must be mapped");
        byKey.Should().BeGreaterThanOrEqualTo(0, "/work-items/by-key/{key} must be mapped");
        byId.Should().BeGreaterThanOrEqualTo(0);
        assignable.Should().BeLessThan(byId,
            "the literal segment is registered first — relying on the :guid constraint for "
            + "disambiguation is the trap the acceptance-rules /defaults comment warns about");
        byKey.Should().BeLessThan(byId);
    }

    [Test]
    public void No_api_tasks_route_is_introduced()
    {
        // Story 39-19 owns /api/tasks (the decision inbox). The two surfaces
        // must stay distinguishable in a route table (44-2 technical notes).
        AllRoutes().Should().NotBeEmpty("the route table must actually have been read");
        AllRoutes().Should().NotContain(
            r => r.Pattern.StartsWith("/api/tasks", StringComparison.OrdinalIgnoreCase),
            "the tracker is /api/work-items; /api/tasks belongs to 39-19");
    }

    // ══════════════════ AC10 — the catalog descriptors ══════════════════════

    [Test]
    public void Every_mutating_route_has_a_descriptor()
    {
        var mutating = TrackerRoutes()
            .Where(r => r.Method is "POST" or "PUT" or "PATCH" or "DELETE")
            .Select(r => $"{r.Method} {Normalize(r.Pattern)}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        // Bidirectional: the catalog's issue-tracking NATIVE members and the
        // route table must be the same set. A new mutating route with no
        // descriptor fails here; a descriptor whose route was deleted fails
        // here too. This is what 43-8's harness will generalise.
        var catalogued = ActionCatalog.ByGroup[ActionGroup.IssueTracking]
            .Select(k => ActionCatalog.ByKey[k])
            .Where(d => d.Key.Key.StartsWith("tracker.", StringComparison.Ordinal))
            .Select(d => d.SiteKey.Split('—')[0].Trim())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        mutating.Should().HaveCount(10, "AC2's ten mutating tracker routes");
        catalogued.Should().BeEquivalentTo(mutating);

        // …and every one ships behaviour-preserving (nothing gates them today).
        ActionCatalog.ByGroup[ActionGroup.IssueTracking]
            .Select(k => ActionCatalog.ByKey[k])
            .Where(d => d.Key.Key.StartsWith("tracker.", StringComparison.Ordinal))
            .Should().OnlyContain(d => d.DefaultMinAutonomy == AutonomyDial.Min);
    }

    // ── Route-table helpers ────────────────────────────────────────────────

    private sealed record RouteRow(string Method, string Pattern);

    private RouteRow[] AllRoutes()
    {
        // Force the host to build its pipeline: the endpoint data source is
        // empty until the app has been started, so reading it off a lazily
        // constructed factory would make every route assertion pass vacuously.
        using var _ = _factory.CreateClient();

        // Union of BOTH resolution shapes (the GovernanceHostFixture note):
        // resolving the singleton and resolving the enumerable are different
        // code paths, so taking both and de-duplicating means an ASP.NET Core
        // upgrade cannot silently halve the sweep.
        var services = _factory.Services;
        var sources = services.GetServices<EndpointDataSource>().ToList();
        var single = services.GetService<EndpointDataSource>();
        if (single is not null && !sources.Contains(single))
            sources.Add(single);

        return sources
            .SelectMany(s => s.Endpoints)
            .Distinct()
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => new RouteRow(m, Slash(e.RoutePattern.RawText ?? string.Empty))))
            .ToArray();
    }

    /// <summary>The tracker group's own routes, in registration order.</summary>
    private RouteRow[] TrackerRoutes() => AllRoutes()
        .Where(r => r.Pattern.StartsWith("/api/projects", StringComparison.Ordinal)
            || r.Pattern.StartsWith("/api/work-items", StringComparison.Ordinal)
            || r.Pattern.StartsWith("/api/tracker/preferences", StringComparison.Ordinal))
        .ToArray();

    private static int IndexOf(RouteRow[] routes, string method, string pattern) =>
        Array.FindIndex(routes, r => r.Method == method && r.Pattern == pattern);

    /// <summary>Attribute-routed patterns arrive without a leading slash; minimal-API ones with.</summary>
    private static string Slash(string pattern) =>
        pattern.StartsWith('/') ? pattern : "/" + pattern;

    /// <summary>Route pattern → SiteKey shape: constraints stripped.</summary>
    private static string Normalize(string pattern) =>
        System.Text.RegularExpressions.Regex.Replace(pattern, @"\{(\w+):[^}]+\}", "{$1}");
}
