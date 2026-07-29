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

    /// <summary>
    /// A body that does NOT mention <c>acceptorRequirement</c> — the exact shape the
    /// pre-Story-43-0 dashboard dialog sent (eight fields, no ninth). The DTO binds
    /// the missing property to <c>null</c> = "the caller did not say".
    /// </summary>
    private static AcceptanceRulesUpsertRequest Req(int autonomy = 90)
    {
        var r = AcceptanceDefaults.Rules with { AutonomyLevel = autonomy };
        return new AcceptanceRulesUpsertRequest(
            r.AutonomyLevel, r.MaxRevisionRounds, r.MaxValidationRepairAttempts,
            r.AmbiguityEscalationThreshold, r.AlwaysEscalate, r.ReviewerSelection,
            r.DecisionGuidance, r.RoutingGuidance);
    }

    /// <summary>A body that DOES state an acceptor requirement.</summary>
    private static AcceptanceRulesUpsertRequest ReqWithAcceptor(
        AcceptorRequirement acceptor, int autonomy = 90)
        => Req(autonomy) with { AcceptorRequirement = acceptor };

    /// <summary>Deserialize a PUT body straight off the wire, the way the binder does.</summary>
    private static AcceptanceRulesUpsertRequest Bind(string json) =>
        JsonSerializer.Deserialize<AcceptanceRulesUpsertRequest>(
            json, AcceptanceRulesJson.Options)!;

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

    // ── acceptorRequirement: an omitted field is preserved, never invented (43-0) ──

    /// <summary>
    /// THE regression pin for Story 43-0's live bug. The admin dialog PUT a body with
    /// eight fields and no <c>acceptorRequirement</c>; the DTO's old non-nullable
    /// <c>= AcceptorRequirement.Any</c> default invented `any`, so the first save of
    /// <c>design</c> for ANY unrelated reason destroyed its shipped human-acceptor
    /// floor (<c>AcceptanceDefaults.For(Design)</c> ships
    /// <see cref="AcceptorRequirement.Human"/>). Fails on the pre-fix code.
    /// </summary>
    [Test]
    public async Task Upsert_omitting_acceptorRequirement_preserves_shipped_human_floor()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(userId, "owner");

        // Sanity: design ships `human` before anyone edits anything.
        AcceptanceDefaults.For(DocumentTypeKey.Design).AcceptorRequirement
            .Should().Be(AcceptorRequirement.Human);

        // An UNRELATED edit (autonomy only), body silent about acceptorRequirement.
        var result = await AcceptanceRulesEndpoints.Upsert(
            "design", Req(85), _store, user, tc, Mode(TammaMode.SingleUser));
        (await Exec(result)).Status.Should().Be(StatusCodes.Status200OK);

        var resolved = await _store.ResolveAsync(userId, DocumentTypeKey.Design);
        resolved.Source.Should().Be(AcceptanceRulesSource.TypeOverride);
        resolved.Rules.AutonomyLevel.Should().Be(85);
        resolved.Rules.AcceptorRequirement.Should().Be(
            AcceptorRequirement.Human,
            "a PUT body that never mentions acceptorRequirement must not reset it (Story 43-0)");
    }

    /// <summary>The same preservation for a STORED override, not just a shipped default.</summary>
    [Test]
    public async Task Upsert_omitting_acceptorRequirement_preserves_a_stored_human_override()
    {
        var tenantId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);
        var admin = Principal(Guid.NewGuid(), "admin");

        // `plan` ships `any`; an admin deliberately pins it to `human`.
        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "plan", ReqWithAcceptor(AcceptorRequirement.Human, 90),
            _store, admin, tc, Mode(TammaMode.SaaS)));
        (await _store.ResolveForTenantAsync(tenantId, DocumentTypeKey.Plan))
            .Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);

        // A later edit that says nothing about the acceptor leaves it pinned.
        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "plan", Req(96), _store, admin, tc, Mode(TammaMode.SaaS)));

        var resolved = await _store.ResolveForTenantAsync(tenantId, DocumentTypeKey.Plan);
        resolved.Rules.AutonomyLevel.Should().Be(96);
        resolved.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);
    }

    /// <summary>AC3 case (a): a body that STATES `human` round-trips as `human`.</summary>
    [Test]
    public async Task Upsert_stated_acceptorRequirement_human_round_trips()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(userId, "owner");

        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "test-spec", ReqWithAcceptor(AcceptorRequirement.Human),
            _store, user, tc, Mode(TammaMode.SingleUser)));

        (await _store.ResolveAsync(userId, DocumentTypeKey.TestSpec))
            .Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);
    }

    /// <summary>
    /// AC3 case (b), adapted to the preserve semantics: for a type whose effective
    /// requirement IS <c>any</c>, an omitting body still writes <c>any</c> — the
    /// documented pre-39-13 behavior is unchanged for every type that never had a
    /// human floor. (What changed is only that omission now means "keep what is in
    /// force" instead of the literal constant `any`.)
    /// </summary>
    [Test]
    public async Task Upsert_omitting_acceptorRequirement_on_an_any_type_stays_any()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(userId, "owner");

        AcceptanceDefaults.For(DocumentTypeKey.Findings).AcceptorRequirement
            .Should().Be(AcceptorRequirement.Any);

        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "findings", Req(88), _store, user, tc, Mode(TammaMode.SingleUser)));

        (await _store.ResolveAsync(userId, DocumentTypeKey.Findings))
            .Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Any);
    }

    /// <summary>
    /// An admin can still LOWER the floor — but only by saying so. `any` stated
    /// explicitly is honoured; that is the difference between silence and intent.
    /// </summary>
    [Test]
    public async Task Upsert_explicit_any_clears_the_human_floor()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(userId, "owner");

        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "design", ReqWithAcceptor(AcceptorRequirement.Any, 85),
            _store, user, tc, Mode(TammaMode.SingleUser)));

        (await _store.ResolveAsync(userId, DocumentTypeKey.Design))
            .Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Any);
    }

    /// <summary>
    /// The binder-level pin: absent property → <c>null</c> ("not said"), never
    /// <c>Any</c>. This is the property the old DTO default violated; a future
    /// reader who re-adds `= AcceptorRequirement.Any` fails here first.
    /// </summary>
    [Test]
    public void Bind_body_without_acceptorRequirement_yields_null_not_any()
    {
        var eightFieldBody = """
        {
          "autonomyLevel": 85,
          "maxRevisionRounds": 3,
          "maxValidationRepairAttempts": 2,
          "ambiguityEscalationThreshold": 0.7,
          "alwaysEscalate": [],
          "reviewerSelection": {
            "mode": "single-reviewer",
            "reviewerRole": "architect",
            "panelRoles": [],
            "quorum": null,
            "decisionRule": "unanimous"
          },
          "decisionGuidance": "g",
          "routingGuidance": "r"
        }
        """;

        Bind(eightFieldBody).AcceptorRequirement.Should().BeNull();
        Bind(eightFieldBody.Replace("\"decisionGuidance\": \"g\"",
                "\"acceptorRequirement\": \"human\", \"decisionGuidance\": \"g\""))
            .AcceptorRequirement.Should().Be(AcceptorRequirement.Human);
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
