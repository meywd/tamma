using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.AcceptanceRules;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.AcceptanceRules;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Data;

namespace Tamma.Api.Tests.AcceptanceRules;

/// <summary>
/// Endpoint dispatch + RBAC-matrix tests for <see cref="AcceptanceRulesEndpoints"/>
/// (Story 39-5 AC6, AC7). Mirrors <c>PromptEndpointsTenantAdminTests</c>: uses the
/// EF-InMemory fixture and drives the endpoint delegates directly. HTTP-level 403
/// enforcement rides the shared <c>AcceptanceRulesManage</c> policy — pinned via
/// the permission matrix (the dev-mode permissive auth seam short-circuits named
/// policies, exactly as documented for the prompt store).
/// </summary>
[TestFixture]
public class AcceptanceRulesEndpointsTests
{
    private InMemoryDbFixture _fx = null!;
    private AcceptanceRulesService _store = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new InMemoryDbFixture();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var repo = new Tamma.Data.Repositories.AcceptanceRulesRepository(_fx.Factory, tc);
        _store = new AcceptanceRulesService(repo);
    }

    [TearDown]
    public async Task TearDown() => await _fx.DisposeAsync();

    private static ClaimsPrincipal Principal(Guid userId, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ITammaModeProvider Mode(TammaMode mode) => new StubModeProvider(mode);
    private sealed class StubModeProvider(TammaMode mode) : ITammaModeProvider { public TammaMode Mode { get; } = mode; }

    private static AcceptanceRulesUpsertRequest Req(int autonomy = 90)
    {
        var r = AcceptanceDefaults.Rules with { AutonomyLevel = autonomy };
        return new AcceptanceRulesUpsertRequest(
            r.AutonomyLevel, r.MaxRevisionRounds, r.MaxValidationRepairAttempts,
            r.AmbiguityEscalationThreshold, r.AlwaysEscalate, r.ReviewerSelection,
            r.DecisionGuidance, r.RoutingGuidance);
    }

    private static async Task<(int Status, string Body)> Exec(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, body);
    }

    // ── SaaS: admin PUT writes a tenant-keyed row ──

    [Test]
    public async Task Upsert_SaaS_writes_tenant_keyed_row()
    {
        var tenantId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);
        var admin = Principal(Guid.NewGuid(), "admin");

        var result = await AcceptanceRulesEndpoints.Upsert(
            "plan", Req(97), _store, admin, tc, Mode(TammaMode.SaaS));
        (await Exec(result)).Status.Should().Be(StatusCodes.Status200OK);

        var resolved = await _store.ResolveForTenantAsync(tenantId, DocumentTypeKey.Plan);
        resolved.Source.Should().Be(AcceptanceRulesSource.TypeOverride);
        resolved.Rules.AutonomyLevel.Should().Be(97);
    }

    // ── single-user: sole user writes a user-keyed row ──

    [Test]
    public async Task Upsert_SingleUser_writes_user_keyed_row_and_resolves()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(userId, "owner");

        var result = await AcceptanceRulesEndpoints.Upsert(
            "design", Req(85), _store, user, tc, Mode(TammaMode.SingleUser));
        (await Exec(result)).Status.Should().Be(StatusCodes.Status200OK);

        var resolved = await _store.ResolveAsync(userId, DocumentTypeKey.Design);
        resolved.Source.Should().Be(AcceptanceRulesSource.TypeOverride);
        resolved.Rules.AutonomyLevel.Should().Be(85);
    }

    // ── the `base` dial row ──

    [Test]
    public async Task Upsert_base_writes_principal_base_row()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(userId, "owner");

        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "base", Req(80), _store, user, tc, Mode(TammaMode.SingleUser)));

        // A type with no per-type override now resolves from the base row.
        var resolved = await _store.ResolveAsync(userId, DocumentTypeKey.Findings);
        resolved.Source.Should().Be(AcceptanceRulesSource.PrincipalDefault);
        resolved.Rules.AutonomyLevel.Should().Be(80);
    }

    // ── validation + unknown-key → 400 ──

    [Test]
    public async Task Upsert_invalid_autonomy_is_400_with_code()
    {
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(Guid.NewGuid(), "owner");

        var (status, body) = await Exec(await AcceptanceRulesEndpoints.Upsert(
            "plan", Req(150), _store, user, tc, Mode(TammaMode.SingleUser)));

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("ACCEPTANCE_RULES.INVALID");
    }

    [Test]
    public async Task Upsert_unknown_type_key_is_400()
    {
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(Guid.NewGuid(), "owner");

        var (status, body) = await Exec(await AcceptanceRulesEndpoints.Upsert(
            "not-a-type", Req(), _store, user, tc, Mode(TammaMode.SingleUser)));

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("DOCUMENT.TYPE.UNKNOWN");
    }

    // ── reads ──

    [Test]
    public async Task GetResolved_member_read_is_200()
    {
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var member = Principal(Guid.NewGuid(), "member");

        var (status, _) = await Exec(await AcceptanceRulesEndpoints.GetResolved(
            "review", _store, member, tc, Mode(TammaMode.SaaS)));
        status.Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task GetDefaults_is_read_only_200()
    {
        var (status, body) = await Exec(AcceptanceRulesEndpoints.GetDefaults());
        status.Should().Be(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("autonomyLevel").GetInt32().Should().Be(70);
    }

    // ── RBAC matrix pin (AC7) ──

    [Test]
    public void AcceptanceRulesManage_permission_is_admin_and_owner_not_member()
    {
        Permissions.Matrix.Should().ContainKey("acceptance-rules:manage");
        Permissions.HasPermission("owner", "acceptance-rules:manage").Should().BeTrue();
        Permissions.HasPermission("admin", "acceptance-rules:manage").Should().BeTrue();
        Permissions.HasPermission("member", "acceptance-rules:manage").Should().BeFalse();
    }
}
