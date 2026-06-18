using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-1 (AC10, AC12, AC13) — <see cref="PlanCatalogService"/> against a
/// real Postgres testcontainer (so the value converter, FK, and partial unique
/// index behave like production). Covers snapshot resolution (features +
/// entitlements + prices assembled per version), active-only by slug, frozen
/// deprecated snapshot by id, the tenant <c>PlanId</c> shadow-column resolution
/// + isolation, and the one-active-version DB invariant.
/// </summary>
[TestFixture]
public class PlanCatalogServiceTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("plan_catalog_test")
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
            "TRUNCATE plan_prices, plan_entitlements, plan_features, plans, tenants CASCADE;");
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private async Task SeedAsync()
    {
        await using var ctx = NewContext();
        await PlansSeeder.SeedAsync(ctx);
    }

    private PlanCatalogService BuildService(ControlPlaneDbContext ctx) =>
        new(ctx, NullLogger<PlanCatalogService>.Instance);

    [Test]
    public async Task GetActiveBySlug_Assembles_Full_Snapshot()
    {
        await SeedAsync();
        await using var ctx = NewContext();
        var svc = BuildService(ctx);

        var snap = await svc.GetActiveBySlugAsync("team");

        snap.Should().NotBeNull();
        snap!.Slug.Should().Be("team");
        snap.Version.Should().Be(1);
        snap.Status.Should().Be("active");
        snap.Features.Should().NotBeEmpty();
        snap.Entitlements.Should().Contain(e => e.MetricKey == EntitlementMetricKey.LlmTokens);
        snap.Prices.Select(p => p.PricingMode)
            .Should().BeEquivalentTo(new[] { "platform_provided", "byok" });
    }

    [Test]
    public async Task GetActiveBySlug_Unknown_Returns_Null()
    {
        await SeedAsync();
        await using var ctx = NewContext();
        var svc = BuildService(ctx);

        (await svc.GetActiveBySlugAsync("nope")).Should().BeNull();
    }

    [Test]
    public async Task GetById_Returns_Frozen_Deprecated_Snapshot()
    {
        await SeedAsync();

        // Supersede team v1 → v2 (v1 becomes deprecated, frozen).
        Guid v1Id;
        await using (var editCtx = NewContext())
        {
            var editor = new PlanVersionEditor(
                editCtx, new RecordingPlatformEventPublisher(),
                TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            v1Id = (await editCtx.Plans.FirstAsync(p => p.Slug == "team" && p.Status == "active")).Id;
            await editor.CreateNewVersionAsync(
                "team",
                new PlanDraftSpec(DisplayName: "Team v2"),
                new PlanEditorPrincipal("u", "u@x.io"));
        }

        await using var ctx = NewContext();
        var svc = BuildService(ctx);

        var byId = await svc.GetByIdAsync(v1Id);
        byId.Should().NotBeNull();
        byId!.Version.Should().Be(1);
        byId.Status.Should().Be("deprecated", "the prior version is frozen, still snapshot-able by id");

        var active = await svc.GetActiveBySlugAsync("team");
        active!.Version.Should().Be(2);
        active.DisplayName.Should().Be("Team v2");
    }

    [Test]
    public async Task GetForTenant_Resolves_Via_PlanId_ShadowColumn()
    {
        await SeedAsync();

        var tenantId = Guid.NewGuid();
        await using (var setup = NewContext())
        {
            var tenant = NewTenant(tenantId, "free");
            setup.Tenants.Add(tenant);
            setup.Entry(tenant).Property("PlanId").CurrentValue = PlansSeeder.FreePlanId;
            await setup.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var svc = BuildService(ctx);

        var snap = await svc.GetForTenantAsync(tenantId);
        snap.Should().NotBeNull();
        snap!.Slug.Should().Be("free");
    }

    [Test]
    public async Task GetForTenant_With_No_Plan_Returns_Null()
    {
        await SeedAsync();

        var tenantId = Guid.NewGuid();
        await using (var setup = NewContext())
        {
            setup.Tenants.Add(NewTenant(tenantId, "free"));
            // Deliberately leave PlanId shadow column NULL.
            await setup.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var svc = BuildService(ctx);

        (await svc.GetForTenantAsync(tenantId)).Should().BeNull();
    }

    [Test]
    public async Task GetForTenant_Isolation_Two_Tenants_Resolve_Their_Own_Plan()
    {
        await SeedAsync();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await using (var setup = NewContext())
        {
            var a = NewTenant(tenantA, "free");
            var b = NewTenant(tenantB, "team");
            setup.Tenants.Add(a);
            setup.Tenants.Add(b);
            setup.Entry(a).Property("PlanId").CurrentValue = PlansSeeder.FreePlanId;
            setup.Entry(b).Property("PlanId").CurrentValue = PlansSeeder.TeamPlanId;
            await setup.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var svc = BuildService(ctx);

        (await svc.GetForTenantAsync(tenantA))!.Slug.Should().Be("free");
        (await svc.GetForTenantAsync(tenantB))!.Slug.Should().Be("team",
            "the catalog is platform-global; each tenant resolves only its own assigned plan");
    }

    [Test]
    public async Task ListActive_Returns_Only_Active_Versions()
    {
        await SeedAsync();

        await using (var editCtx = NewContext())
        {
            var editor = new PlanVersionEditor(
                editCtx, new RecordingPlatformEventPublisher(),
                TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            await editor.CreateNewVersionAsync(
                "team", new PlanDraftSpec(), new PlanEditorPrincipal("u", null));
        }

        await using var ctx = NewContext();
        var svc = BuildService(ctx);

        var list = await svc.ListActiveAsync();
        list.Should().HaveCount(3, "still one active version per slug after the team supersede");
        list.Should().OnlyContain(s => s.Status == "active");
        list.Single(s => s.Slug == "team").Version.Should().Be(2);
    }

    [Test]
    public async Task OneActiveVersion_Invariant_Rejects_Second_Active_Row()
    {
        await SeedAsync();

        await using var ctx = NewContext();
        var dup = new Plan
        {
            Id = Guid.NewGuid(),
            Slug = "team",
            DisplayName = "Rogue duplicate",
            Version = 99,
            Status = "active", // a SECOND active row for slug "team"
            BillingInterval = "monthly",
            PlacementPolicy = "shared",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Plans.Add(dup);

        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "UX_plans_OneActivePerSlug forbids two active versions for one slug");
    }

    private static Tenant NewTenant(Guid id, string plan) => new()
    {
        Id = id,
        Name = "T-" + id.ToString("N")[..6],
        Slug = "t-" + id.ToString("N")[..6],
        Type = "team",
        Plan = plan,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
