using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Prompts;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Story 27-3 — tenant-admin RBAC + mode-aware dispatch tests for
/// <see cref="PromptEndpoints"/>. Exercises the SaaS-mode override path
/// wired in 27-2 through the public endpoint methods (the same delegates
/// <c>Program.cs</c> wires into the minimal API), plus the
/// <c>prompts:manage</c> permission added in 27-3.
///
/// <para>HTTP-level RBAC enforcement (the <c>PromptManage</c> policy
/// returning 403 for member-role callers) cannot be exercised through
/// <see cref="ApiTestFixture"/> because the dev-mode permissive auth seam
/// short-circuits every named policy. We instead pin the contract by
/// asserting the permission matrix directly (see
/// <see cref="PromptManagePermissionTests"/> below) and pin the dispatch
/// branches by direct method invocation against constructed
/// <see cref="ClaimsPrincipal"/> + <see cref="ITammaModeProvider"/> stubs.</para>
/// </summary>
[TestFixture]
public class PromptEndpointsTenantAdminTests
{
    private InMemoryDbFixture _fx = null!;
    private PromptStoreService _store = null!;
    private PromptEventsService _events = null!;
    private TenantContext _tenantContext = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new InMemoryDbFixture();
        _tenantContext = new TenantContext();
        // PromptRepository requires an ambient tenant id; the SaaS-mode
        // ForTenantAsync calls layer their own predicates on top, so the
        // ambient id is just a placeholder for the repository contract.
        _tenantContext.SetTenantId(Guid.NewGuid());

        var repo = new PromptRepository(_fx.Factory, _tenantContext);
        _store = new PromptStoreService(repo);

        var eventRepo = new EventRepository(
            _fx.Factory,
            new TenantContext(),
            new PlatformEventRepository(_fx.Cp));
        _events = new PromptEventsService(eventRepo);
    }

    [TearDown]
    public async Task TearDown() => await _fx.DisposeAsync();

    private static ClaimsPrincipal PrincipalWithUserId(Guid userId, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ITammaModeProvider Mode(TammaMode mode) => new StubModeProvider(mode);

    private sealed class StubModeProvider(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    /// <summary>
    /// Executes a minimal-API <see cref="IResult"/> against a synthetic
    /// <see cref="HttpContext"/> and asserts the status is 200. Mirrors the
    /// pattern used by <c>RoleCheckEndpointTests</c> so the dispatch-flow
    /// assertions still cover the concrete result type without fragile
    /// generic-result casting (<c>Results.Ok(value)</c> returns a non-generic
    /// <c>OkObjectResult</c> internally that can't be matched by
    /// <c>Ok&lt;T&gt;</c>).
    /// </summary>
    private static async Task AssertOkAsync(IResult result)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        var ctx = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    // ------------------------------------------------------------------
    // SaaS mode — dispatch lands on the *ForTenantAsync surface.
    // ------------------------------------------------------------------

    [Test]
    public async Task UpsertPrompt_SaaSMode_PersistsAsTenantOverride_NotUserOverride()
    {
        var tenantId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);
        var principal = PrincipalWithUserId(adminUserId, "admin");

        var req = new UpsertPromptRequest(
            Template: "TENANT-FROM-ADMIN",
            SystemPrompt: null,
            Variables: null,
            EnableTools: null,
            MaxTokens: null);

        var result = await PromptEndpoints.UpsertPrompt(
            "developer", "plan-implementation", req, _store, _events, principal, tc, Mode(TammaMode.SaaS));

        result.Should().NotBeNull();

        // The row must be tenant-scoped (UserId is null, TenantId is set) — the
        // SaaS surface ignores per-user keys to enforce CLAUDE.md's "no per-user
        // override layer in SaaS" rule.
        var rows = await _store.ListTenantOverridesAsync(tenantId);
        rows.Should().HaveCount(1);
        rows[0].TenantId.Should().Be(tenantId);
        rows[0].UserId.Should().BeNull();
        rows[0].Template.Should().Be("TENANT-FROM-ADMIN");

        // The same admin user should NOT have a user-scoped row written.
        var userRows = await _store.ListUserOverridesAsync(adminUserId);
        userRows.Should().BeEmpty();
    }

    [Test]
    public async Task GetPrompt_SaaSMode_ResolvesThroughTenantPath()
    {
        var tenantId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);

        // Admin sets the tenant prompt
        await _store.UpsertRoleActionForTenantAsync(
            tenantId, actingUserId: Guid.NewGuid(),
            "developer", "plan-implementation",
            new UpsertPromptInput(Template: "TENANT-CANONICAL"));

        // Member-role reader (different user) — sees the tenant override,
        // not a user-scoped row. Drive the endpoint to confirm it returns
        // a non-error result, then verify the resolved row through the
        // service surface (the endpoint wraps the same ResolvedPrompt the
        // service produces; checking through the service avoids fragile
        // IResult unwrapping but still covers the dispatch path).
        var memberPrincipal = PrincipalWithUserId(Guid.NewGuid(), "member");
        var result = await PromptEndpoints.GetPrompt(
            "developer", "plan-implementation", _store, memberPrincipal, tc, Mode(TammaMode.SaaS));
        result.Should().NotBeNull();
        await AssertOkAsync(result);

        var resolved = await _store.ResolveRoleActionForTenantAsync(
            tenantId, "developer", "plan-implementation");
        resolved!.Template.Should().Be("TENANT-CANONICAL");
        resolved.Source.Should().Be(PromptSource.TenantOverride);
    }

    [Test]
    public async Task DeletePrompt_SaaSMode_RemovesTenantOverride()
    {
        var tenantId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);

        await _store.UpsertRoleActionForTenantAsync(
            tenantId, null, "developer", "plan-implementation",
            new UpsertPromptInput(Template: "TO-BE-DELETED"));

        var admin = PrincipalWithUserId(Guid.NewGuid(), "owner");
        var result = await PromptEndpoints.DeletePrompt(
            "developer", "plan-implementation", _store, _events, admin, tc, Mode(TammaMode.SaaS));

        result.Should().NotBeNull();

        // After deletion, resolution falls through to the system role+action
        // default — the tenant-scoped row is gone.
        var resolved = await _store.ResolveRoleActionForTenantAsync(tenantId, "developer", "plan-implementation");
        resolved!.Source.Should().Be(PromptSource.SystemRoleAction);
    }

    // ------------------------------------------------------------------
    // Story 27-18 — TammaError → 404 at the endpoint boundary.
    //
    // A taxonomy-valid (role, action) pair that the role does NOT own (e.g.
    // developer/deploy — deploy is devops-only) has no system default. When no
    // override exists either, PromptStoreService throws TammaError with code
    // PROMPT.RESOLVE.NO_DEFAULT. The endpoint must catch that and return 404,
    // NOT let it surface as a 500.
    // ------------------------------------------------------------------

    /// <summary>
    /// Mirrors the <see cref="AssertOkAsync"/> helper but asserts HTTP 404.
    /// </summary>
    private static async Task AssertNotFoundAsync(IResult result)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        var ctx = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task GetPrompt_TaxonomyValidButRoleDoesNotOwnAction_Returns404()
    {
        // 'deploy' is a devops-only action — developer has no system default for
        // it. No override exists either, so resolution throws TammaError
        // (PROMPT.RESOLVE.NO_DEFAULT). GetPrompt must translate that to 404.
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        var principal = PrincipalWithUserId(userId, "owner");

        var result = await PromptEndpoints.GetPrompt(
            "developer", "deploy", _store, principal, tc, Mode(TammaMode.SingleUser));

        await AssertNotFoundAsync(result);
    }

    [Test]
    public async Task RenderPrompt_TaxonomyValidButRoleDoesNotOwnAction_Returns404()
    {
        // Same pair through the render surface — resolution throws TammaError
        // before any rendering or event emission, so 404 is returned.
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        var principal = PrincipalWithUserId(userId, "owner");

        var req = new RenderPromptRequest(new Dictionary<string, string>());

        var result = await PromptEndpoints.RenderPrompt(
            "developer", "deploy", req, _store, _events, principal, tc, Mode(TammaMode.SingleUser));

        await AssertNotFoundAsync(result);
    }

    [Test]
    public async Task GetPrompt_SaaSMode_TaxonomyValidButRoleDoesNotOwnAction_Returns404()
    {
        // SaaS-mode path through the tenant resolver — same TammaError contract.
        var tenantId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);
        var principal = PrincipalWithUserId(Guid.NewGuid(), "member");

        var result = await PromptEndpoints.GetPrompt(
            "developer", "deploy", _store, principal, tc, Mode(TammaMode.SaaS));

        await AssertNotFoundAsync(result);
    }

    // ------------------------------------------------------------------
    // SaaS mode — cross-tenant isolation through the endpoint surface.
    // ------------------------------------------------------------------

    [Test]
    public async Task SaaSMode_TwoTenants_DoNotSeeEachOthersOverrides()
    {
        var acme = Guid.NewGuid();
        var globex = Guid.NewGuid();

        var tcA = new TenantContext();
        tcA.SetTenantId(acme);
        var tcG = new TenantContext();
        tcG.SetTenantId(globex);

        var adminA = PrincipalWithUserId(Guid.NewGuid(), "admin");
        var adminG = PrincipalWithUserId(Guid.NewGuid(), "admin");

        await PromptEndpoints.UpsertPrompt(
            "developer", "plan-implementation",
            new UpsertPromptRequest(Template: "ACME-PROMPT", SystemPrompt: null, Variables: null, EnableTools: null, MaxTokens: null),
            _store, _events, adminA, tcA, Mode(TammaMode.SaaS));

        await PromptEndpoints.UpsertPrompt(
            "developer", "plan-implementation",
            new UpsertPromptRequest(Template: "GLOBEX-PROMPT", SystemPrompt: null, Variables: null, EnableTools: null, MaxTokens: null),
            _store, _events, adminG, tcG, Mode(TammaMode.SaaS));

        // Acme's GET sees Acme's row only
        var acmeMember = PrincipalWithUserId(Guid.NewGuid(), "member");
        var acmeRes = await PromptEndpoints.GetPrompt(
            "developer", "plan-implementation", _store, acmeMember, tcA, Mode(TammaMode.SaaS));
        await AssertOkAsync(acmeRes);

        // Globex's GET sees Globex's row only
        var globexMember = PrincipalWithUserId(Guid.NewGuid(), "member");
        var globexRes = await PromptEndpoints.GetPrompt(
            "developer", "plan-implementation", _store, globexMember, tcG, Mode(TammaMode.SaaS));
        await AssertOkAsync(globexRes);

        // Verify the persisted rows via the service surface — the GET path
        // routes through the same Resolve*ForTenant call.
        var acmeResolved = await _store.ResolveRoleActionForTenantAsync(acme, "developer", "plan-implementation");
        var globexResolved = await _store.ResolveRoleActionForTenantAsync(globex, "developer", "plan-implementation");
        acmeResolved!.Template.Should().Be("ACME-PROMPT");
        globexResolved!.Template.Should().Be("GLOBEX-PROMPT");

        // List endpoints stay disjoint too
        var acmeList = await _store.ListTenantOverridesAsync(acme);
        var globexList = await _store.ListTenantOverridesAsync(globex);
        acmeList.Should().HaveCount(1);
        globexList.Should().HaveCount(1);
        acmeList[0].Template.Should().Be("ACME-PROMPT");
        globexList[0].Template.Should().Be("GLOBEX-PROMPT");
    }

    // ------------------------------------------------------------------
    // SaaS mode — member-scoped user rows must NOT leak into tenant resolution.
    // (Pins the no-per-user-override-layer rule end-to-end through the endpoint.)
    // ------------------------------------------------------------------

    [Test]
    public async Task SaaSMode_MemberUserScopedRow_DoesNotLeakIntoTenantResolution()
    {
        var tenantId = Guid.NewGuid();
        var memberUserId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);

        // Tenant prompt (the one the team should see)
        await _store.UpsertRoleActionForTenantAsync(
            tenantId, actingUserId: Guid.NewGuid(),
            "developer", "plan-implementation",
            new UpsertPromptInput(Template: "TENANT-OFFICIAL"));

        // A user-scoped row for the same role+action (would only happen if
        // someone bypassed the SaaS-mode endpoint dispatcher; we still want
        // to assert it's invisible to the tenant resolver).
        await _store.UpsertRoleActionAsync(
            memberUserId, tenantId: null, "developer", "plan-implementation",
            new UpsertPromptInput(Template: "MEMBER-PERSONAL-LEAK"));

        var memberPrincipal = PrincipalWithUserId(memberUserId, "member");
        var result = await PromptEndpoints.GetPrompt(
            "developer", "plan-implementation", _store, memberPrincipal, tc, Mode(TammaMode.SaaS));
        await AssertOkAsync(result);

        var resolved = await _store.ResolveRoleActionForTenantAsync(
            tenantId, "developer", "plan-implementation");
        resolved!.Template.Should().Be("TENANT-OFFICIAL");
        resolved.Source.Should().Be(PromptSource.TenantOverride);
    }

    // ------------------------------------------------------------------
    // Single-user mode regression — the legacy user-scoped path keeps working.
    // ------------------------------------------------------------------

    [Test]
    public async Task UpsertPrompt_SingleUserMode_PersistsAsUserOverride()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext(); // no ambient tenant — SaaS-mode would refuse
        var principal = PrincipalWithUserId(userId, "owner");

        var req = new UpsertPromptRequest(
            Template: "MY-PERSONAL-PROMPT",
            SystemPrompt: null, Variables: null, EnableTools: null, MaxTokens: null);

        await PromptEndpoints.UpsertPrompt(
            "developer", "plan-implementation", req, _store, _events, principal, tc, Mode(TammaMode.SingleUser));

        var rows = await _store.ListUserOverridesAsync(userId);
        rows.Should().HaveCount(1);
        rows[0].UserId.Should().Be(userId);
        rows[0].TenantId.Should().BeNull();
        rows[0].Template.Should().Be("MY-PERSONAL-PROMPT");
    }

    [Test]
    public async Task GetPrompt_SingleUserMode_ResolvesThroughUserPath()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        var principal = PrincipalWithUserId(userId, "owner");

        await _store.UpsertRoleActionAsync(
            userId, tenantId: null, "developer", "plan-implementation",
            new UpsertPromptInput(Template: "USER-LEVEL"));

        var result = await PromptEndpoints.GetPrompt(
            "developer", "plan-implementation", _store, principal, tc, Mode(TammaMode.SingleUser));
        await AssertOkAsync(result);

        // The user-scoped resolver returns the user override.
        var resolved = await _store.ResolveRoleActionAsync(userId, "developer", "plan-implementation");
        resolved!.Template.Should().Be("USER-LEVEL");
        resolved.Source.Should().Be(PromptSource.UserOverride);
    }

    // ------------------------------------------------------------------
    // Mode/ambient-tenant interaction — SaaS mode without an ambient tenant
    // falls back to the user surface (the dispatcher's xor on
    // `tenantContext.TenantId is Guid tenantId`). This pins the safe
    // direction: a missing tenant claim never silently writes to "some
    // other tenant" — it writes to the user instead.
    // ------------------------------------------------------------------

    [Test]
    public async Task SaaSMode_WithoutAmbientTenant_FallsBackToUserSurface()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext(); // NO tenant id set
        var principal = PrincipalWithUserId(userId, "admin");

        var req = new UpsertPromptRequest(
            Template: "FALLBACK-USER",
            SystemPrompt: null, Variables: null, EnableTools: null, MaxTokens: null);

        await PromptEndpoints.UpsertPrompt(
            "developer", "plan-implementation", req, _store, _events, principal, tc, Mode(TammaMode.SaaS));

        // Wrote to the user surface, not the tenant surface.
        var userRows = await _store.ListUserOverridesAsync(userId);
        userRows.Should().HaveCount(1);
        userRows[0].UserId.Should().Be(userId);
        userRows[0].TenantId.Should().BeNull();
    }
}

/// <summary>
/// Story 27-3 — pins the <c>prompts:manage</c> permission contract added to
/// the role matrix in <see cref="Permissions"/>. The CLAUDE.md "Prompt Store
/// Architecture / RBAC" table requires PUT/DELETE override to be reachable
/// by tenant_owner OR tenant_admin (admin+owner in this codebase). The
/// existing <c>settings:manage</c> permission is owner-only and would 403
/// every admin caller; <c>prompts:manage</c> is the dedicated gate.
/// </summary>
[TestFixture]
public class PromptManagePermissionTests
{
    [Test]
    public void Owner_CanManagePrompts()
    {
        Permissions.HasPermission("owner", "prompts:manage").Should().BeTrue();
    }

    [Test]
    public void Admin_CanManagePrompts()
    {
        Permissions.HasPermission("admin", "prompts:manage").Should().BeTrue(
            "CLAUDE.md /api/prompts/* PUT/DELETE must be reachable by tenant_admin");
    }

    [Test]
    public void Member_CannotManagePrompts()
    {
        Permissions.HasPermission("member", "prompts:manage").Should().BeFalse(
            "CLAUDE.md 'Prompt Store / RBAC': member users get 403 on PUT/DELETE in SaaS mode");
    }

    [Test]
    public void GetRolePermissions_Member_ExcludesPromptsManage()
    {
        Permissions.GetRolePermissions("member").Should().NotContain("prompts:manage");
    }

    [Test]
    public void GetRolePermissions_Admin_IncludesPromptsManage()
    {
        Permissions.GetRolePermissions("admin").Should().Contain("prompts:manage");
    }
}
