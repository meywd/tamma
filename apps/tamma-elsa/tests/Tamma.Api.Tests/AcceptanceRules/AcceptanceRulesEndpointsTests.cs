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

        // Sanity: below design's level (45) its DERIVED floor is `human` (Story
        // 43-16 — the floor is no longer a stored constant on For(Design)).
        AcceptanceFloors.ShippedFloorFor(DocumentTypeKey.Design, 40)
            .Should().Be(AcceptorRequirement.Human);

        // Put the dial below design's level so its human floor is in force.
        (await Exec(await AcceptanceRulesEndpoints.Upsert(
            "base", Req(40), _store, user, tc, Mode(TammaMode.SingleUser))))
            .Status.Should().Be(StatusCodes.Status200OK);

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

    // ── CD-1 / amendment A1 (closed 2026-07-30): the BASE route cannot erase a
    //    shipped human acceptor floor, and a later per-type save cannot bake the
    //    loss in ──────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CD-1 regression pin, and the reason it is NOT the same bug as 43-0's.
    /// It fires on an EXPLICIT value, not just an omitted one: 43-0's
    /// preserve-on-absent works correctly on the base route (it carries the BASE
    /// row's own in-force requirement forward) — the loss came from 39-5 D2's
    /// tier-2 WHOLESALE shadowing, which made one base row shadow the shipped
    /// per-type defaults entirely. One PUT used to strip the human acceptor from
    /// design, sprint-plan AND threat-model at once, none of which was written.
    /// </summary>
    [Test]
    public async Task Upsert_base_cannot_erase_the_shipped_human_acceptor_floor()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(userId, "owner");

        // The most hostile shape: the base PUT STATES `any` outright. Dial 40 is
        // BELOW every human-pinned type's level (design/threat-model 45,
        // sprint-plan 95), so the DERIVED floor (Story 43-16) is Human for all
        // three — the discriminating position where CD-1 is demonstrable.
        (await Exec(await AcceptanceRulesEndpoints.Upsert(
            "base", ReqWithAcceptor(AcceptorRequirement.Any, 40),
            _store, user, tc, Mode(TammaMode.SingleUser))))
            .Status.Should().Be(StatusCodes.Status200OK);

        foreach (var type in new[]
        {
            DocumentTypeKey.Design, DocumentTypeKey.SprintPlan, DocumentTypeKey.ThreatModel,
        })
        {
            var resolved = await _store.ResolveAsync(userId, type);
            resolved.Source.Should().Be(AcceptanceRulesSource.PrincipalDefault,
                "the base row still supplies every OTHER field wholesale (39-5 D2 stands)");
            resolved.Rules.AutonomyLevel.Should().Be(40, "…including the dial it was written for");
            resolved.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human,
                $"'{type.ToWire()}' ships a human acceptor FLOOR — a base row, which stands "
                + "in for every document type at once, cannot express intent about this one "
                + "and therefore cannot lower it (CD-1)");
            resolved.AcceptorRequirementFloored.Should().BeTrue(
                "the raise is surfaced, not silent");
        }

        // A type with no shipped floor is untouched by any of this.
        var findings = await _store.ResolveAsync(userId, DocumentTypeKey.Findings);
        findings.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Any);
        findings.AcceptorRequirementFloored.Should().BeFalse();
    }

    /// <summary>
    /// The second half of CD-1: a later OMITTING per-type save used to read the
    /// already-degraded value as "what is in force" and bake it into a type row,
    /// after which deleting the base row no longer restored the floor. With the
    /// floor applied at resolution, the value 43-0's preserve-on-absent reads is
    /// the floored one, so the bake-in writes `human`.
    /// </summary>
    [Test]
    public async Task A_later_omitting_per_type_save_cannot_bake_in_a_lost_floor()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(userId, "owner");

        // Base dial 40 is below design's level (45), so its derived human floor is
        // in force (Story 43-16) — the value preserve-on-absent must carry forward.
        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "base", ReqWithAcceptor(AcceptorRequirement.Any, 40),
            _store, user, tc, Mode(TammaMode.SingleUser)));

        // An unrelated per-type edit, silent about acceptorRequirement.
        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "design", Req(85), _store, user, tc, Mode(TammaMode.SingleUser)));

        var withBase = await _store.ResolveAsync(userId, DocumentTypeKey.Design);
        withBase.Source.Should().Be(AcceptanceRulesSource.TypeOverride);
        withBase.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);

        // …and deleting the base row still restores everything, because nothing
        // was ever degraded to bake in.
        await Exec(await AcceptanceRulesEndpoints.Delete(
            "base", _store, user, tc, Mode(TammaMode.SingleUser)));
        (await _store.ResolveAsync(userId, DocumentTypeKey.Design))
            .Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);
    }

    /// <summary>
    /// The product decision, pinned from the other side: the floor is not
    /// absolute — a PER-TYPE route may still lower it, because writing that row
    /// NAMES the type. (The pre-existing
    /// <c>Upsert_explicit_any_clears_the_human_floor</c> pins the same semantic;
    /// this one pins that a base row cannot then re-raise it back.)
    /// </summary>
    [Test]
    public async Task An_explicit_per_type_any_still_wins_over_the_base_floor()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(userId, "owner");

        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "design", ReqWithAcceptor(AcceptorRequirement.Any, 85),
            _store, user, tc, Mode(TammaMode.SingleUser)));
        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "base", Req(80), _store, user, tc, Mode(TammaMode.SingleUser)));

        var resolved = await _store.ResolveAsync(userId, DocumentTypeKey.Design);
        resolved.Source.Should().Be(AcceptanceRulesSource.TypeOverride);
        resolved.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Any,
            "lowering a shipped human floor requires naming the type — and that "
            + "deliberate act is not undone by the floor (tier 1 is exempt)");
        resolved.AcceptorRequirementFloored.Should().BeFalse();
    }

    /// <summary>
    /// The SaaS path takes the identical rule — a tenant base row is one row for
    /// every document type, exactly like the single-user one.
    ///
    /// <para>Review 3.2/3.3 (2026-07-30): this used to assert ONE document type
    /// (<c>threat-model</c>) while the single-user path had four tests. The code
    /// IS symmetric — <c>ResolveAsync</c> and <c>ResolveForTenantAsync</c> apply
    /// <see cref="AcceptanceFloors.ApplyShippedAcceptorFloor"/> on the same tier-2
    /// branch and exempt tier 1 identically — but symmetric code is a reason to
    /// EXPECT the tests to agree, not a substitute for them. SaaS now mirrors
    /// single-user: all three human-pinned types plus a control here, the bake-in
    /// path and the per-type-<c>any</c> exemption below.</para>
    /// </summary>
    [Test]
    public async Task Upsert_base_cannot_erase_the_human_floor_in_SaaS_mode_either()
    {
        var tenantId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);
        var user = Principal(Guid.NewGuid(), "owner");

        // Dial 40 is below every human-pinned type's level — the derived floor is
        // Human on the tenant path exactly as on the user path (Story 43-16).
        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "base", ReqWithAcceptor(AcceptorRequirement.Any, 40),
            _store, user, tc, Mode(TammaMode.SaaS)));

        foreach (var type in new[]
        {
            DocumentTypeKey.Design, DocumentTypeKey.SprintPlan, DocumentTypeKey.ThreatModel,
        })
        {
            var resolved = await _store.ResolveForTenantAsync(tenantId, type);
            resolved.Source.Should().Be(AcceptanceRulesSource.PrincipalDefault,
                "the tenant base row still supplies every OTHER field wholesale");
            resolved.Rules.AutonomyLevel.Should().Be(40);
            resolved.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human,
                $"'{type.ToWire()}' ships a human acceptor FLOOR, and a TENANT base row is "
                + "just as unable to express intent about one type as a user base row is");
            resolved.AcceptorRequirementFloored.Should().BeTrue();
        }

        // The control: a type with no shipped floor is untouched in SaaS too.
        var findings = await _store.ResolveForTenantAsync(tenantId, DocumentTypeKey.Findings);
        findings.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Any);
        findings.AcceptorRequirementFloored.Should().BeFalse();
    }

    /// <summary>
    /// The SaaS mirror of <see cref="A_later_omitting_per_type_save_cannot_bake_in_a_lost_floor"/>:
    /// 43-0's preserve-on-absent reads "what is in force", so if the floor were
    /// missing on the tenant path a later omitting per-type PUT would bake the
    /// loss into a tenant type row and deleting the base row would no longer
    /// restore it.
    /// </summary>
    [Test]
    public async Task A_later_omitting_per_type_save_cannot_bake_in_a_lost_floor_in_SaaS_mode()
    {
        var tenantId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);
        var user = Principal(Guid.NewGuid(), "owner");

        // Base dial 40 keeps design's derived human floor in force (Story 43-16).
        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "base", ReqWithAcceptor(AcceptorRequirement.Any, 40),
            _store, user, tc, Mode(TammaMode.SaaS)));

        // An unrelated per-type edit, silent about acceptorRequirement.
        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "design", Req(85), _store, user, tc, Mode(TammaMode.SaaS)));

        var withBase = await _store.ResolveForTenantAsync(tenantId, DocumentTypeKey.Design);
        withBase.Source.Should().Be(AcceptanceRulesSource.TypeOverride);
        withBase.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);

        await Exec(await AcceptanceRulesEndpoints.Delete(
            "base", _store, user, tc, Mode(TammaMode.SaaS)));
        (await _store.ResolveForTenantAsync(tenantId, DocumentTypeKey.Design))
            .Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);
    }

    /// <summary>
    /// The SaaS mirror of the tier-1 EXEMPTION: a tenant admin may still lower a
    /// shipped human floor, but only by naming the type. If this ever starts
    /// failing, the floor has become absolute and a real capability was removed.
    /// </summary>
    [Test]
    public async Task An_explicit_per_type_any_still_wins_over_the_base_floor_in_SaaS_mode()
    {
        var tenantId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(tenantId);
        var user = Principal(Guid.NewGuid(), "owner");

        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "design", ReqWithAcceptor(AcceptorRequirement.Any, 85),
            _store, user, tc, Mode(TammaMode.SaaS)));
        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "base", Req(80), _store, user, tc, Mode(TammaMode.SaaS)));

        var resolved = await _store.ResolveForTenantAsync(tenantId, DocumentTypeKey.Design);
        resolved.Source.Should().Be(AcceptanceRulesSource.TypeOverride);
        resolved.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Any,
            "tier 1 is exempt on the tenant path exactly as on the user path");
        resolved.AcceptorRequirementFloored.Should().BeFalse();

        // …and an unwritten sibling type keeps its floor, so the exemption is
        // per-type rather than a blanket switch-off.
        var sprintPlan = await _store.ResolveForTenantAsync(tenantId, DocumentTypeKey.SprintPlan);
        sprintPlan.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);
        sprintPlan.AcceptorRequirementFloored.Should().BeTrue();
    }

    /// <summary>
    /// The scope of the fix, stated as a test so it is not mis-read: reviewer
    /// selection is STILL shadowed wholesale by a base row. There is no ordering
    /// on reviewer roles — no <c>max(architect, security)</c> — so no monotone
    /// floor exists for it, and a deployment-wide reviewer choice is a legitimate
    /// thing for a base row to say. Recorded in 43-0 A1 / epic-43 CD-1 as the
    /// deliberate remainder.
    /// </summary>
    [Test]
    public async Task Base_row_still_shadows_per_type_reviewer_selection_by_design()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var user = Principal(userId, "owner");

        AcceptanceDefaults.For(DocumentTypeKey.ThreatModel).ReviewerSelection.ReviewerRole
            .Should().Be("security");

        // Base dial 40 is below threat-model's level (45), so its derived human
        // acceptor floor bites while the reviewer selection is still shadowed.
        await Exec(await AcceptanceRulesEndpoints.Upsert(
            "base", Req(40), _store, user, tc, Mode(TammaMode.SingleUser)));

        var resolved = await _store.ResolveAsync(userId, DocumentTypeKey.ThreatModel);
        resolved.Rules.ReviewerSelection.ReviewerRole.Should().Be("architect",
            "wholesale tier-2 precedence is unchanged for every field except the "
            + "acceptor floor — this is the documented remainder of CD-1, not an oversight");
        resolved.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human,
            "…while the safety-critical field that DOES have a lattice survives");
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

    // ══════════ Review MODERATE-2 — a corrupt stored row is a 400 ═══════════
    // Story 43-0 made Upsert READ before writing (so an omitted
    // acceptorRequirement is preserved). That introduced a NEW 500 on a shipped
    // admin surface: Materialize → AcceptanceRulesJson.Deserialize throws
    // JsonException on malformed stored JSON, and the endpoint caught only
    // TammaError. Worse, per-type resolution FALLS THROUGH to the base row, so
    // ONE corrupt base row broke PUT for EVERY document type. Before 43-0,
    // Upsert never read — overwriting WAS the repair.

    /// <summary>Write a row the store cannot parse, straight past the service.</summary>
    private async Task PoisonRowAsync(Guid userId, string? documentTypeKey)
    {
        await using var db = await _fx.Factory.CreateAsync(Guid.NewGuid());
        db.AcceptanceRulesOverrides.Add(new Tamma.Data.Entities.AcceptanceRulesOverride
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantId = null,
            DocumentTypeKey = documentTypeKey,
            RulesJson = "{ this is not json",
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Upsert_over_a_malformed_stored_row_is_400_naming_the_problem_not_500()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        await PoisonRowAsync(userId, "design");

        var (status, body) = await Exec(await AcceptanceRulesEndpoints.Upsert(
            "design", Req(85), _store, Principal(userId, "owner"), tc, Mode(TammaMode.SingleUser)));

        status.Should().Be(StatusCodes.Status400BadRequest,
            "a stored row this API cannot parse is a repairable state, not an opaque server fault");
        body.Should().Contain("ACCEPTANCE_RULES.STORED_ROW_UNREADABLE");
        body.Should().Contain("DELETE", "the response must name the repair path");
    }

    [Test]
    public async Task One_malformed_BASE_row_makes_every_type_400_not_500()
    {
        // The blast radius that made this a MAJOR-shaped 500: nothing is wrong
        // with `plan` or `test-plan` — they fall through to the poisoned base.
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        await PoisonRowAsync(userId, null);

        foreach (var key in new[] { "design", "plan", "test-plan" })
        {
            var (status, body) = await Exec(await AcceptanceRulesEndpoints.Upsert(
                key, Req(85), _store, Principal(userId, "owner"), tc, Mode(TammaMode.SingleUser)));
            status.Should().Be(StatusCodes.Status400BadRequest, $"key={key}");
            body.Should().Contain("ACCEPTANCE_RULES.STORED_ROW_UNREADABLE");
            body.Should().Contain("base",
                "the message must point at the BASE row, because that is the one that is corrupt "
                + "even though the caller addressed a document type");
        }
    }

    [Test]
    public async Task Get_resolved_over_a_malformed_stored_row_is_400_not_500()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        await PoisonRowAsync(userId, "design");

        var (status, body) = await Exec(await AcceptanceRulesEndpoints.GetResolved(
            "design", _store, Principal(userId, "owner"), tc, Mode(TammaMode.SingleUser)));

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("ACCEPTANCE_RULES.STORED_ROW_UNREADABLE");
    }

    [Test]
    public async Task Delete_then_put_recovers_from_a_malformed_stored_row()
    {
        var userId = Guid.NewGuid();
        var tc = new TenantContext();
        tc.SetTenantId(Guid.NewGuid());
        var principal = Principal(userId, "owner");
        await PoisonRowAsync(userId, "design");

        // DELETE never reads the body, so it is the repair.
        (await Exec(await AcceptanceRulesEndpoints.Delete(
            "design", _store, principal, tc, Mode(TammaMode.SingleUser))))
            .Status.Should().Be(StatusCodes.Status200OK);

        // A base dial below design's level (45) puts its derived human floor in
        // force (Story 43-16), so the repaired omitting save preserves it.
        (await Exec(await AcceptanceRulesEndpoints.Upsert(
            "base", Req(40), _store, principal, tc, Mode(TammaMode.SingleUser))))
            .Status.Should().Be(StatusCodes.Status200OK);

        // …and the PUT that previously 400'd now succeeds.
        (await Exec(await AcceptanceRulesEndpoints.Upsert(
            "design", Req(85), _store, principal, tc, Mode(TammaMode.SingleUser))))
            .Status.Should().Be(StatusCodes.Status200OK);

        var resolved = await _store.ResolveAsync(userId, DocumentTypeKey.Design);
        resolved.Source.Should().Be(AcceptanceRulesSource.TypeOverride);
        resolved.Rules.AutonomyLevel.Should().Be(85);
        resolved.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human,
            "and the repaired save still preserves design's shipped human floor — the whole "
            + "point of 43-0; the fall-back tier supplied it once the corrupt row was gone");
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
