using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Dtos.Pricing;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-2 (AC1/AC2/AC14) — the PUBLIC plan-catalog read routes
/// (<see cref="PricingEndpoints.ListPublicPlans"/> /
/// <see cref="PricingEndpoints.GetPublicPlanBySlug"/>). Proves the public list
/// surfaces active, non-custom plans and excludes deprecated / draft / custom
/// plans, and that a custom plan's slug is never resolvable through the public
/// route (tenant-isolation by construction).
/// </summary>
[TestFixture]
public class PublicPricingPlansEndpointsTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("public_pricing_plans_test")
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
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE plan_prices, plan_entitlements, plan_features, plans CASCADE;");
        await PlansSeeder.SeedAsync(ctx);
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private PlanCatalogService Catalog(ControlPlaneDbContext db) =>
        new(db, NullLogger<PlanCatalogService>.Instance);

    private static IReadOnlyList<PlanSnapshot> PlansFrom(IResult result)
    {
        var value = ((IValueHttpResult)result).Value!;
        var plans = value.GetType().GetProperty("plans")!.GetValue(value)!;
        return ((IEnumerable<PlanSnapshot>)plans).ToList();
    }

    // ── AC1 — public list returns the seeded active public plans ──
    [Test]
    public async Task ListPublicPlans_Returns_Active_Public_Only()
    {
        await using var db = NewContext();
        var result = await PricingEndpoints.ListPublicPlans(Catalog(db), default);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status200OK);
        var plans = PlansFrom(result);
        plans.Select(p => p.Slug).Should().BeEquivalentTo(new[] { "free", "team", "enterprise" });
        plans.Should().OnlyContain(p => p.Status == "active" && !p.IsCustom);
    }

    // ── AC1 — deprecated versions are excluded ──
    [Test]
    public async Task ListPublicPlans_Excludes_Deprecated()
    {
        // Version "team" so v1 flips to deprecated.
        await using (var ctx = NewContext())
        {
            var editor = new PlanVersionEditor(
                ctx, new RecordingPlatformEventPublisher(),
                TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            await editor.CreateNewVersionAsync(
                "team", new PlanDraftSpec(DisplayName: "Team v2"),
                new PlanEditorPrincipal("u", "e@x.io"));
        }

        await using var db = NewContext();
        var plans = PlansFrom(await PricingEndpoints.ListPublicPlans(Catalog(db), default));
        plans.Where(p => p.Slug == "team").Should().ContainSingle()
            .Which.Version.Should().Be(2, "only the active (v2) team plan is public; the deprecated v1 is excluded");
    }

    // ── AC1 — draft plans are excluded ──
    [Test]
    public async Task ListPublicPlans_Excludes_Draft()
    {
        await using (var ctx = NewContext())
        {
            ctx.Plans.Add(new Plan
            {
                Id = Guid.NewGuid(),
                Slug = "preview",
                DisplayName = "Preview",
                Version = 1,
                Status = "draft",
                IsCustom = false,
                BillingInterval = "monthly",
                Quotas = "{}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var db = NewContext();
        var plans = PlansFrom(await PricingEndpoints.ListPublicPlans(Catalog(db), default));
        plans.Should().NotContain(p => p.Slug == "preview");
    }

    // ── AC5 — custom plans never appear in the public list ──
    [Test]
    public async Task ListPublicPlans_Excludes_Custom()
    {
        var tenantId = Guid.NewGuid();
        string customSlug;
        await using (var ctx = NewContext())
        {
            var editor = new PlanVersionEditor(
                ctx, new RecordingPlatformEventPublisher(),
                TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            var plan = await editor.CreateCustomVersionAsync(
                tenantId, new PlanDraftSpec(DisplayName: "Bespoke"),
                new PlanEditorPrincipal("u", "e@x.io"));
            customSlug = plan.Slug;
        }

        await using var db = NewContext();
        var plans = PlansFrom(await PricingEndpoints.ListPublicPlans(Catalog(db), default));
        plans.Should().NotContain(p => p.IsCustom);
        plans.Should().NotContain(p => p.Slug == customSlug);
    }

    // ── AC2 — get by slug returns the active public plan; unknown → 404 ──
    [Test]
    public async Task GetPublicPlanBySlug_Returns_Active_Or_404()
    {
        await using var db = NewContext();
        var ok = await PricingEndpoints.GetPublicPlanBySlug("team", Catalog(db), default);
        ((IStatusCodeHttpResult)ok).StatusCode.Should().Be(StatusCodes.Status200OK);

        var missing = await PricingEndpoints.GetPublicPlanBySlug("nope", Catalog(db), default);
        ((IStatusCodeHttpResult)missing).StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // ── AC2/AC14 — a custom plan's slug is never resolvable publicly ──
    [Test]
    public async Task GetPublicPlanBySlug_Custom_Returns404()
    {
        string customSlug;
        await using (var ctx = NewContext())
        {
            var editor = new PlanVersionEditor(
                ctx, new RecordingPlatformEventPublisher(),
                TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            var plan = await editor.CreateCustomVersionAsync(
                Guid.NewGuid(), new PlanDraftSpec(DisplayName: "Bespoke"),
                new PlanEditorPrincipal("u", "e@x.io"));
            customSlug = plan.Slug;
        }

        await using var db = NewContext();
        var result = await PricingEndpoints.GetPublicPlanBySlug(customSlug, Catalog(db), default);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}
