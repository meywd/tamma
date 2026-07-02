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
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-9 — <see cref="AdminPricingDashboardEndpoints.GetOverview"/> against a
/// real Postgres testcontainer. Pins the read-only aggregation contract: the
/// admin catalog rows (every status + custom), each version's live
/// <c>active</c>-tenant assignment count (counted by the version-pinned PlanId,
/// so a deprecated version still shows its stranded tenants), and the
/// active-only margin-config rollup. Also covers the empty-state (AC11 — no
/// throw when nothing is seeded). The PlatformOwnerAccess 403 gate is pinned by
/// <c>PlatformOwnerAccessPolicyTests</c>, which lists /api/admin/pricing/overview.
/// </summary>
[TestFixture]
public class AdminPricingDashboardEndpointsTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("admin_pricing_dashboard_test")
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
        await ctx.Database.ExecuteSqlRawAsync("TRUNCATE tenant_plan_assignments CASCADE;");
        await ctx.Database.ExecuteSqlRawAsync("TRUNCATE tenants CASCADE;");
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE plan_prices, plan_entitlements, plan_features, plans CASCADE;");
        await ctx.Database.ExecuteSqlRawAsync("TRUNCATE margin_policies CASCADE;");
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private static ClaimsPrincipal Actor() =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "user-34-9"),
            new Claim(JwtRegisteredClaimNames.Email, "owner@tamma.dev"),
            new Claim("platformRole", "platform_admin"),
        }, "test"));

    private static void AssertStatus(IResult result, int expected)
    {
        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(expected);
    }

    private static PlanCatalogService Catalog(ControlPlaneDbContext db) =>
        new(db, NullLogger<PlanCatalogService>.Instance);

    private static PlanVersionEditor Editor(ControlPlaneDbContext db, RecordingPlatformEventPublisher pub) =>
        new(db, pub, TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);

    private static CreatePlanRequest ValidCreate(string slug, decimal recurringUsd = 49m) => new(
        slug, $"{slug} plan", "monthly",
        Features: new[] { new PlanFeatureDto("byok_allowed", true, null) },
        Entitlements: new[] { new PlanEntitlementDto("seats", 5, "total", "block") },
        Prices: new[] { new PlanPriceDto("platform_provided", recurringUsd, 15m, null) });

    /// <summary>Create a public plan version through the real editor; return its (PlanId, Version).</summary>
    private async Task<(Guid PlanId, int Version)> CreatePublicPlanAsync(string slug, decimal recurringUsd = 49m)
    {
        await using var db = NewContext();
        var result = await AdminPlanCatalogEndpoints.CreatePlan(
            ValidCreate(slug, recurringUsd), Editor(db, new RecordingPlatformEventPublisher()),
            Catalog(db), Actor(), default);
        AssertStatus(result, StatusCodes.Status201Created);

        var row = await db.Plans.AsNoTracking().SingleAsync(p => p.Slug == slug && p.Status == "active");
        return (row.Id, row.Version);
    }

    /// <summary>Seed a tenant + an assignment row pinned to (planId, planVersion).</summary>
    private async Task SeedAssignmentAsync(Guid planId, int planVersion, string status = "active")
    {
        await using var db = NewContext();
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"t-{Guid.NewGuid():N}",
            Slug = $"t-{Guid.NewGuid():N}",
            Type = "personal",
            Plan = "free",           // legacy column is CHECK-constrained to seeded slugs
            Settings = "{}",
            CreatedAt = now,
            UpdatedAt = now,
            ProvisioningState = "none",
        };
        db.Tenants.Add(tenant);
        db.TenantPlanAssignments.Add(new TenantPlanAssignment
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            PlanId = planId,
            PlanVersion = planVersion,
            Status = status,
            EffectiveFrom = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedGlobalMarginAsync(decimal multiplier)
    {
        await using var db = NewContext();
        await AdminPricingEndpoints.VersionMargin(
            new VersionMarginRequest("global", null, multiplier, null),
            db, new RecordingPlatformEventPublisher(), Actor(), TimeProvider.System, default);
    }

    private async Task<PricingOverviewResponse> GetOverviewAsync()
    {
        await using var db = NewContext();
        var result = await AdminPricingDashboardEndpoints.GetOverview(Catalog(db), db, default);
        AssertStatus(result, StatusCodes.Status200OK);
        return (PricingOverviewResponse)((IValueHttpResult)result).Value!;
    }

    // ── Empty state (AC11) — nothing seeded must not throw and returns zeros ──
    [Test]
    public async Task GetOverview_EmptyCatalog_ReturnsEmpty_NoThrow()
    {
        var overview = await GetOverviewAsync();

        overview.Plans.Should().BeEmpty();
        overview.Totals.ActivePlanCount.Should().Be(0);
        overview.Totals.CustomPlanCount.Should().Be(0);
        overview.Totals.TotalActiveAssignments.Should().Be(0);
        overview.Totals.PlansWithActiveAssignments.Should().Be(0);
        overview.Margins.ActivePolicyCount.Should().Be(0);
        overview.Margins.GlobalMarkupMultiplier.Should().BeNull();
    }

    // ── Core aggregation — plans, per-plan counts, margins, totals ──
    [Test]
    public async Task GetOverview_Aggregates_Plans_Assignments_And_Margins()
    {
        var (planA, verA) = await CreatePublicPlanAsync("startup", 49m);
        var (planB, verB) = await CreatePublicPlanAsync("team", 199m);

        // Two tenants on A, one on B, one CANCELLED on A (must NOT be counted).
        await SeedAssignmentAsync(planA, verA);
        await SeedAssignmentAsync(planA, verA);
        await SeedAssignmentAsync(planB, verB);
        await SeedAssignmentAsync(planA, verA, status: "cancelled");

        await SeedGlobalMarginAsync(1.3m);

        var overview = await GetOverviewAsync();

        overview.Plans.Should().HaveCount(2);
        var rowA = overview.Plans.Single(p => p.Slug == "startup");
        rowA.ActiveTenantCount.Should().Be(2);
        rowA.RecurringUsd.Should().Be(49m);
        rowA.IsCustom.Should().BeFalse();
        rowA.Status.Should().Be("active");

        overview.Plans.Single(p => p.Slug == "team").ActiveTenantCount.Should().Be(1);

        overview.Totals.ActivePlanCount.Should().Be(2);
        overview.Totals.TotalActiveAssignments.Should().Be(3); // cancelled excluded
        overview.Totals.PlansWithActiveAssignments.Should().Be(2);

        overview.Margins.ActivePolicyCount.Should().Be(1);
        overview.Margins.GlobalPolicyCount.Should().Be(1);
        overview.Margins.GlobalMarkupMultiplier.Should().Be(1.3m);
    }

    // ── Custom plans are surfaced + flagged (dashboard shows the whole catalog) ──
    [Test]
    public async Task GetOverview_IncludesCustomPlan_FlaggedAndCounted()
    {
        // A bound tenant for the custom plan.
        var tenantId = Guid.NewGuid();
        await using (var db = NewContext())
        {
            var now = DateTime.UtcNow;
            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "enterprise-co",
                Slug = "enterprise-co",
                Type = "org",
                Plan = "free",
                Settings = "{}",
                CreatedAt = now,
                UpdatedAt = now,
                ProvisioningState = "none",
            });
            await db.SaveChangesAsync();
        }

        await CreatePublicPlanAsync("startup");

        await using (var db = NewContext())
        {
            var result = await AdminPlanCatalogEndpoints.CreateCustomPlan(
                new CreateCustomPlanRequest(
                    tenantId, "Bespoke Enterprise", "monthly",
                    Features: null,
                    Entitlements: new[] { new PlanEntitlementDto("seats", null, "total", "allow") },
                    Prices: new[] { new PlanPriceDto("platform_provided", 5000m, 0m, null) }),
                Editor(db, new RecordingPlatformEventPublisher()), Catalog(db), Actor(), default);
            AssertStatus(result, StatusCodes.Status201Created);
        }

        var overview = await GetOverviewAsync();

        overview.Plans.Should().Contain(p => p.IsCustom);
        overview.Plans.Count(p => p.IsCustom).Should().Be(1);
        overview.Totals.CustomPlanCount.Should().Be(1);
        // The public "startup" plan is not double-counted as custom.
        overview.Totals.ActivePlanCount.Should().Be(1);
    }

    // ── Deprecated version still counts its version-pinned tenants ──
    [Test]
    public async Task GetOverview_DeprecatedVersion_StillCountsPinnedTenants()
    {
        var (planV1, ver1) = await CreatePublicPlanAsync("pro");
        await SeedAssignmentAsync(planV1, ver1); // a tenant stuck on v1

        // Version the plan → v1 becomes deprecated, a fresh v2 is active.
        await using (var db = NewContext())
        {
            var result = await AdminPlanCatalogEndpoints.VersionPlan(
                "pro", new VersionPlanRequest(DisplayName: "pro v2"),
                Editor(db, new RecordingPlatformEventPublisher()), Catalog(db), Actor(), default);
            AssertStatus(result, StatusCodes.Status200OK);
        }

        var overview = await GetOverviewAsync();

        var v1 = overview.Plans.Single(p => p.PlanId == planV1);
        v1.Status.Should().Be("deprecated");
        v1.ActiveTenantCount.Should().Be(1);

        var v2 = overview.Plans.Single(p => p.Slug == "pro" && p.Status == "active");
        v2.ActiveTenantCount.Should().Be(0);

        overview.Totals.ActivePlanCount.Should().Be(1);        // only v2 is active+public
        overview.Totals.DeprecatedPlanCount.Should().Be(1);    // v1
        overview.Totals.TotalActiveAssignments.Should().Be(1); // the stranded tenant
    }

    // ── Margin rollup reflects only the ACTIVE policy after a supersede ──
    [Test]
    public async Task GetOverview_MarginRollup_ExcludesSupersededPolicies()
    {
        await SeedGlobalMarginAsync(1.3m);
        await SeedGlobalMarginAsync(1.5m); // supersedes 1.3

        var overview = await GetOverviewAsync();

        overview.Margins.ActivePolicyCount.Should().Be(1);
        overview.Margins.GlobalMarkupMultiplier.Should().Be(1.5m);
    }
}
