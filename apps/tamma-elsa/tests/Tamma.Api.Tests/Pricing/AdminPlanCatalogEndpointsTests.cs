using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Dtos.Pricing;
using Tamma.Api.Endpoints.Admin;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-2 — <see cref="AdminPlanCatalogEndpoints"/> against a real Postgres
/// testcontainer. Covers create/version/deprecate/custom-mint over the immutable
/// <see cref="PlanVersionEditor"/>, input validation → 422/400, the
/// deprecate-with-assignments 409 + force path, the custom-public rejection,
/// and the PLAN.CATALOG.UPDATED / PLAN.CUSTOM.CREATED DCB emits. (The
/// PlatformOwnerAccess 403 gate is pinned by <c>PlatformOwnerAccessPolicyTests</c>,
/// which lists /api/admin/pricing/plans.)
/// </summary>
[TestFixture]
public class AdminPlanCatalogEndpointsTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("admin_plan_catalog_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync("TRUNCATE tenants CASCADE;");
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE plan_prices, plan_entitlements, plan_features, plans CASCADE;");
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private static ClaimsPrincipal Actor(string userId = "user-34-2", string email = "owner@tamma.dev") =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("platformRole", "platform_admin"),
        }, "test"));

    private static void AssertStatus(IResult result, int expected)
    {
        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(expected);
    }

    // Instantiate the handler dependencies over ONE context (mirrors the scoped
    // wiring: editor writes + catalog reads share the request-scoped context).
    private async Task<IResult> CreatePlan(CreatePlanRequest body, RecordingPlatformEventPublisher pub)
    {
        await using var db = NewContext();
        return await AdminPlanCatalogEndpoints.CreatePlan(
            body, Editor(db, pub), Catalog(db), Actor(), default);
    }

    private async Task<IResult> VersionPlan(string slug, VersionPlanRequest body, RecordingPlatformEventPublisher pub)
    {
        await using var db = NewContext();
        return await AdminPlanCatalogEndpoints.VersionPlan(
            slug, body, Editor(db, pub), Catalog(db), Actor(), default);
    }

    private async Task<IResult> CreateCustomPlan(CreateCustomPlanRequest body, RecordingPlatformEventPublisher pub)
    {
        await using var db = NewContext();
        return await AdminPlanCatalogEndpoints.CreateCustomPlan(
            body, Editor(db, pub), Catalog(db), Actor(), default);
    }

    private async Task<IResult> DeprecateVersion(string slug, int version, bool? force, RecordingPlatformEventPublisher pub)
    {
        await using var db = NewContext();
        return await AdminPlanCatalogEndpoints.DeprecateVersion(
            slug, version, force, Editor(db, pub), Actor(), default);
    }

    private static PlanVersionEditor Editor(ControlPlaneDbContext db, RecordingPlatformEventPublisher pub) =>
        new(db, pub, TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);

    private static PlanCatalogService Catalog(ControlPlaneDbContext db) =>
        new(db, NullLogger<PlanCatalogService>.Instance);

    private static CreatePlanRequest ValidCreate(string slug) => new(
        slug, $"{slug} plan", "monthly",
        Features: new[] { new PlanFeatureDto("byok_allowed", true, null) },
        Entitlements: new[] { new PlanEntitlementDto("seats", 5, "total", "block") },
        Prices: new[] { new PlanPriceDto("platform_provided", 49m, 15m, null) });

    private async Task<Guid> ActivePlanIdAsync(string slug, int version)
    {
        await using var db = NewContext();
        return (await db.Plans.AsNoTracking()
            .SingleAsync(p => p.Slug == slug && p.Version == version)).Id;
    }

    private async Task PinTenantToAsync(Guid planId, string slug)
    {
        await using var db = NewContext();
        var t = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = slug,
            Slug = slug,
            Type = "personal",
            // Legacy slug column is CHECK-constrained to the seeded slugs; the
            // version pin under test is the PlanId shadow column set below.
            Plan = "free",
            Settings = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ProvisioningState = "none",
        };
        db.Tenants.Add(t);
        db.Entry(t).Property("PlanId").CurrentValue = planId;
        await db.SaveChangesAsync();
    }

    // ── AC3 — create initial version ──
    [Test]
    public async Task CreatePlan_Writes_V1_Active_And_Emits_CatalogUpdated_Created()
    {
        var pub = new RecordingPlatformEventPublisher();
        var result = await CreatePlan(ValidCreate("startup"), pub);
        AssertStatus(result, StatusCodes.Status201Created);

        await using var db = NewContext();
        var row = await db.Plans.AsNoTracking().SingleAsync(p => p.Slug == "startup");
        row.Version.Should().Be(1);
        row.Status.Should().Be("active");
        row.IsCustom.Should().BeFalse();

        var evt = pub.Events.Should().ContainSingle(e => e.Type == PlanCatalogEventTypes.CatalogUpdated).Subject;
        evt.Tags.Should().Contain("\"action\":\"created\"");
        evt.Tags.Should().Contain("\"actorUserId\":\"user-34-2\"");
    }

    // ── AC3 / AC12 — creating over an existing slug is a 409 ──
    [Test]
    public async Task CreatePlan_ExistingSlug_Returns409()
    {
        var pub = new RecordingPlatformEventPublisher();
        await CreatePlan(ValidCreate("team"), pub);

        var again = await CreatePlan(ValidCreate("team"), pub);
        AssertStatus(again, StatusCodes.Status409Conflict);
    }

    // ── AC8 — invalid metric key → 422 ──
    [Test]
    public async Task CreatePlan_InvalidMetricKey_Returns422()
    {
        var pub = new RecordingPlatformEventPublisher();
        var body = new CreatePlanRequest(
            "badmetric", "Bad", "monthly", null,
            Entitlements: new[] { new PlanEntitlementDto("not_a_metric", 1, "total", "block") },
            Prices: null);
        var result = await CreatePlan(body, pub);
        AssertStatus(result, StatusCodes.Status422UnprocessableEntity);
    }

    // ── AC8 — invalid pricing mode → 422 ──
    [Test]
    public async Task CreatePlan_InvalidPricingMode_Returns422()
    {
        var pub = new RecordingPlatformEventPublisher();
        var body = new CreatePlanRequest(
            "badmode", "Bad", "monthly", null, null,
            Prices: new[] { new PlanPriceDto("free_lunch", 0m, 0m, null) });
        var result = await CreatePlan(body, pub);
        AssertStatus(result, StatusCodes.Status422UnprocessableEntity);
    }

    // ── AC3 — version an existing plan (prior → deprecated), emits catalog + lifecycle events ──
    [Test]
    public async Task VersionPlan_Supersedes_Prior_And_Emits_CatalogUpdated_Versioned()
    {
        await using (var seed = NewContext()) await PlansSeeder.SeedAsync(seed);

        var pub = new RecordingPlatformEventPublisher();
        var result = await VersionPlan("team", new VersionPlanRequest(DisplayName: "Team v2"), pub);
        AssertStatus(result, StatusCodes.Status200OK);

        await using var db = NewContext();
        var active = await db.Plans.CountAsync(p => p.Slug == "team" && p.Status == "active");
        active.Should().Be(1, "exactly one active version per slug");
        var v2 = await db.Plans.AsNoTracking().SingleAsync(p => p.Slug == "team" && p.Version == 2);
        v2.Status.Should().Be("active");

        // The 34-1 lifecycle events AND the 34-2 admin-surface catalog event.
        pub.Events.Should().Contain(e => e.Type == PlanCatalogEventTypes.VersionCreated);
        pub.Events.Should().Contain(e => e.Type == PlanCatalogEventTypes.Deprecated);
        var catalog = pub.Events.Should().ContainSingle(e => e.Type == PlanCatalogEventTypes.CatalogUpdated).Subject;
        catalog.Tags.Should().Contain("\"action\":\"versioned\"");
    }

    // ── AC4 — mint a custom plan bound to a tenant ──
    [Test]
    public async Task CreateCustomPlan_Mints_IsCustom_Bound_To_Tenant_And_Emits_CustomCreated()
    {
        var tenantId = Guid.NewGuid();
        var pub = new RecordingPlatformEventPublisher();
        var body = new CreateCustomPlanRequest(
            tenantId, "Acme Enterprise", "annual",
            Features: new[] { new PlanFeatureDto("support_tier", null, "priority") },
            Entitlements: new[] { new PlanEntitlementDto("llm_tokens", null, "monthly", "meter") },
            Prices: new[] { new PlanPriceDto("platform_provided", 5000m, 0m, null) });

        var result = await CreateCustomPlan(body, pub);
        AssertStatus(result, StatusCodes.Status201Created);

        await using var db = NewContext();
        var row = await db.Plans.AsNoTracking().SingleAsync(p => p.IsCustom);
        row.IsCustom.Should().BeTrue();
        row.Slug.Should().StartWith(CustomPlanSlug.PrefixFor(tenantId));
        CustomPlanSlug.IsBoundTo(row.Slug, tenantId).Should().BeTrue();

        var evt = pub.Events.Should().ContainSingle(e => e.Type == PlanCatalogEventTypes.CustomCreated).Subject;
        evt.Tags.Should().Contain($"\"tenantId\":\"{tenantId:D}\"");
    }

    // ── AC5 — a custom plan asking for public visibility is rejected 400 ──
    [Test]
    public async Task CreateCustomPlan_MakePublic_Returns400()
    {
        var pub = new RecordingPlatformEventPublisher();
        var body = new CreateCustomPlanRequest(
            Guid.NewGuid(), "Sneaky", "monthly", null, null, null, MakePublic: true);
        var result = await CreateCustomPlan(body, pub);
        AssertStatus(result, StatusCodes.Status400BadRequest);

        await using var db = NewContext();
        (await db.Plans.CountAsync()).Should().Be(0, "the rejected custom plan is never written");
    }

    // ── AC11 — custom round-trip: admin list includes it, public list excludes it ──
    [Test]
    public async Task CustomPlan_RoundTrip_AdminLists_PublicExcludes()
    {
        var tenantId = Guid.NewGuid();
        var pub = new RecordingPlatformEventPublisher();
        await CreateCustomPlan(new CreateCustomPlanRequest(
            tenantId, "Bespoke", "monthly", null,
            Entitlements: new[] { new PlanEntitlementDto("seats", 100, "total", "allow") },
            Prices: new[] { new PlanPriceDto("byok", 0m, 0m, null) }), pub);

        await using var db = NewContext();
        var catalog = Catalog(db);

        var admin = await catalog.ListAllForAdminAsync(new PlanListFilter(IsCustom: true));
        admin.Should().ContainSingle(p => p.IsCustom);

        var pub2 = await catalog.ListActivePublicAsync();
        pub2.Should().NotContain(p => p.IsCustom);

        var byTenant = await catalog.ListAllForAdminAsync(new PlanListFilter(TenantId: tenantId));
        byTenant.Should().ContainSingle(p => p.IsCustom);
    }

    // ── AC7 — deprecate with zero assignments → 204 ──
    [Test]
    public async Task DeprecateVersion_NoAssignments_Returns204_And_Flips_Status()
    {
        var pub = new RecordingPlatformEventPublisher();
        await CreatePlan(ValidCreate("solo"), pub);

        var result = await DeprecateVersion("solo", 1, force: false, pub);
        AssertStatus(result, StatusCodes.Status204NoContent);

        await using var db = NewContext();
        var row = await db.Plans.AsNoTracking().SingleAsync(p => p.Slug == "solo" && p.Version == 1);
        row.Status.Should().Be("deprecated");
        pub.Events.Should().Contain(e =>
            e.Type == PlanCatalogEventTypes.CatalogUpdated && e.Tags.Contains("\"action\":\"deprecated\""));
    }

    // ── AC7 — deprecate blocked by assignments → 409; force=true deprecates ──
    [Test]
    public async Task DeprecateVersion_WithAssignments_Returns409_Then_Force_Deprecates()
    {
        var pub = new RecordingPlatformEventPublisher();
        await CreatePlan(ValidCreate("pinned"), pub);
        var planId = await ActivePlanIdAsync("pinned", 1);
        await PinTenantToAsync(planId, "pinned-tenant");

        // Blocked — active assignment, no force.
        var blocked = await DeprecateVersion("pinned", 1, force: false, pub);
        AssertStatus(blocked, StatusCodes.Status409Conflict);
        await using (var db = NewContext())
        {
            var row = await db.Plans.AsNoTracking().SingleAsync(p => p.Slug == "pinned" && p.Version == 1);
            row.Status.Should().Be("active", "blocked deprecate performs no write");
        }

        // Force — deprecates; the tenant stays pinned to the deprecated version.
        var forced = await DeprecateVersion("pinned", 1, force: true, pub);
        AssertStatus(forced, StatusCodes.Status204NoContent);
        await using (var db = NewContext())
        {
            var row = await db.Plans.AsNoTracking().SingleAsync(p => p.Slug == "pinned" && p.Version == 1);
            row.Status.Should().Be("deprecated");
            var stillPinned = await db.Tenants.IgnoreQueryFilters()
                .CountAsync(t => EF.Property<Guid?>(t, "PlanId") == planId);
            stillPinned.Should().Be(1, "immutability rule: existing tenants stay on the deprecated version");
        }
    }

    // ── AC7 — deprecate an unknown (slug, version) → 404 ──
    [Test]
    public async Task DeprecateVersion_Unknown_Returns404()
    {
        var pub = new RecordingPlatformEventPublisher();
        var result = await DeprecateVersion("ghost", 1, force: false, pub);
        AssertStatus(result, StatusCodes.Status404NotFound);
    }

    // ── Admin list filters by status + isCustom ──
    [Test]
    public async Task ListForAdmin_Filters_Status_And_IsCustom()
    {
        var pub = new RecordingPlatformEventPublisher();
        await CreatePlan(ValidCreate("alpha"), pub);
        await CreateCustomPlan(new CreateCustomPlanRequest(
            Guid.NewGuid(), "Beta", "monthly", null, null,
            Prices: new[] { new PlanPriceDto("byok", 0m, 0m, null) }), pub);

        await using var db = NewContext();
        var catalog = Catalog(db);

        (await catalog.ListAllForAdminAsync(new PlanListFilter(Status: "active"))).Should().HaveCount(2);
        (await catalog.ListAllForAdminAsync(new PlanListFilter(IsCustom: false))).Should().ContainSingle(p => p.Slug == "alpha");
        (await catalog.ListAllForAdminAsync(new PlanListFilter(IsCustom: true))).Should().ContainSingle(p => p.IsCustom);
    }
}
