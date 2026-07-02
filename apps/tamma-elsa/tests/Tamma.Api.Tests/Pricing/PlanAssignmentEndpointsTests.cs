using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Endpoints;
using Tamma.Api.Endpoints.Admin;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-4 (AC10-12) — handler-direct tests for the admin PUT/cancel plan
/// routes and the tenant self-service <c>POST /api/pricing/subscribe</c> against
/// a Postgres testcontainer. Covers the happy paths, the draft/deprecated/custom
/// 422s, unknown-tenant 404, and tenant isolation (subscribe resolves the tenant
/// strictly from <see cref="ITenantContext"/>, never a body). The member→403 gate
/// is enforced by the <c>SettingsManage</c> route policy (wired in Program.cs).
/// </summary>
[TestFixture]
public class PlanAssignmentEndpointsTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tpa_endpoint_test")
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
            "TRUNCATE tenant_plan_assignments, plan_prices, plan_entitlements, plan_features, plans, tenants CASCADE;");
        await PlansSeeder.SeedAsync(ctx);
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private sealed class FakeTenantContext : ITenantContext
    {
        public Guid? TenantId { get; private set; }
        public FakeTenantContext(Guid? id) => TenantId = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class FakeModeProvider : ITammaModeProvider
    {
        public FakeModeProvider(TammaMode mode) => Mode = mode;
        public TammaMode Mode { get; }
    }

    private IPlanCatalogService Catalog(ControlPlaneDbContext ctx) =>
        new PlanCatalogService(ctx, NullLogger<PlanCatalogService>.Instance);

    private IPlanAssignmentService Assignments(ControlPlaneDbContext ctx) =>
        new PlanAssignmentService(
            ctx, Catalog(ctx),
            new NullTenantUsageReader(NullLogger<NullTenantUsageReader>.Instance),
            new RecordingPlatformEventPublisher(),
            new RecordingPlatformQueuedTaskRepository(),
            new FakeModeProvider(TammaMode.SaaS),
            TimeProvider.System,
            NullLogger<PlanAssignmentService>.Instance);

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("email", "admin@x.io"),
        }, "test"));

    private static int StatusCodeOf(IResult result)
    {
        if (result is IStatusCodeHttpResult s && s.StatusCode.HasValue) return s.StatusCode.Value;
        throw new InvalidOperationException($"{result.GetType().FullName} exposes no status code.");
    }

    private async Task<Guid> SeedTenantAsync()
    {
        await using var ctx = NewContext();
        var id = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = id,
            Name = "Acme",
            Slug = "acme-" + id.ToString("N")[..6],
            Type = "team",
            Plan = "free",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Tenants.Add(tenant);
        ctx.Entry(tenant).Property("Status").CurrentValue = "active";
        ctx.Entry(tenant).Property("PlanId").CurrentValue = PlansSeeder.FreePlanId;
        ctx.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = new byte[] { 1, 2, 3, 4 };
        await ctx.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> InsertPlanAsync(string slug, string status, bool isCustom)
    {
        await using var ctx = NewContext();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = slug,
            Version = 1,
            Status = status,
            IsCustom = isCustom,
            BillingInterval = "monthly",
            Quotas = "{}",
            IsActive = status == "active",
            PlacementPolicy = "shared",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Plans.Add(plan);
        await ctx.SaveChangesAsync();
        return plan.Id;
    }

    // ── Admin PUT ──

    [Test]
    public async Task PutTenantPlan_Assigns_And_Returns_Version_And_Direction()
    {
        var tenantId = await SeedTenantAsync();
        await using var ctx = NewContext();

        var result = await AdminTenantsEndpoints.PutTenantPlan(
            tenantId, new PutTenantPlanRequest(PlansSeeder.TeamPlanId, Reason: "upgrade"),
            ctx, Assignments(ctx), Principal());

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<PlanAssignmentResponse>>();
        var body = ((Microsoft.AspNetCore.Http.HttpResults.Ok<PlanAssignmentResponse>)result).Value!;
        body.PlanId.Should().Be(PlansSeeder.TeamPlanId);
        body.PlanVersion.Should().Be(1);
        body.Status.Should().Be("active");
        body.Direction.Should().Be("upgrade");
    }

    [Test]
    public async Task PutTenantPlan_UnknownTenant_Returns404()
    {
        await using var ctx = NewContext();
        var result = await AdminTenantsEndpoints.PutTenantPlan(
            Guid.NewGuid(), new PutTenantPlanRequest(PlansSeeder.TeamPlanId),
            ctx, Assignments(ctx), Principal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task PutTenantPlan_DraftPlan_Returns422()
    {
        var tenantId = await SeedTenantAsync();
        var draftId = await InsertPlanAsync("beta", "draft", isCustom: false);
        await using var ctx = NewContext();

        var result = await AdminTenantsEndpoints.PutTenantPlan(
            tenantId, new PutTenantPlanRequest(draftId), ctx, Assignments(ctx), Principal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Test]
    public async Task PutTenantPlan_CustomMisbound_Returns422()
    {
        var tenantId = await SeedTenantAsync();
        var customId = await InsertPlanAsync(CustomPlanSlug.New(Guid.NewGuid()), "active", isCustom: true);
        await using var ctx = NewContext();

        var result = await AdminTenantsEndpoints.PutTenantPlan(
            tenantId, new PutTenantPlanRequest(customId), ctx, Assignments(ctx), Principal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    // ── Admin cancel ──

    [Test]
    public async Task CancelTenantPlan_Schedules_Free_And_Returns200()
    {
        var tenantId = await SeedTenantAsync();
        await using (var ctx = NewContext())
        {
            await AdminTenantsEndpoints.PutTenantPlan(
                tenantId, new PutTenantPlanRequest(PlansSeeder.TeamPlanId),
                ctx, Assignments(ctx), Principal());
        }

        await using var ctx2 = NewContext();
        var result = await AdminTenantsEndpoints.CancelTenantPlan(
            tenantId, new CancelTenantPlanRequest(), ctx2, Assignments(ctx2), Principal());

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<PlanAssignmentResponse>>();
        var body = ((Microsoft.AspNetCore.Http.HttpResults.Ok<PlanAssignmentResponse>)result).Value!;
        body.Status.Should().Be("scheduled");
        body.PlanId.Should().Be(PlansSeeder.FreePlanId);
        body.ScheduledEffectiveAt.Should().NotBeNull();
    }

    // ── Tenant self-service subscribe ──

    [Test]
    public async Task Subscribe_PublicPlan_Returns200_ForCallerTenant()
    {
        var tenantId = await SeedTenantAsync();
        await using var ctx = NewContext();

        var result = await PricingEndpoints.Subscribe(
            new SubscribeRequest("team"),
            Principal(),
            new FakeTenantContext(tenantId),
            new FakeModeProvider(TammaMode.SaaS),
            Catalog(ctx), Assignments(ctx),
            NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<PlanAssignmentResponse>>();
        var body = ((Microsoft.AspNetCore.Http.HttpResults.Ok<PlanAssignmentResponse>)result).Value!;
        body.TenantId.Should().Be(tenantId);
        body.PlanId.Should().Be(PlansSeeder.TeamPlanId);
    }

    [Test]
    public async Task Subscribe_NoActiveTenant_Returns404()
    {
        await using var ctx = NewContext();
        var result = await PricingEndpoints.Subscribe(
            new SubscribeRequest("team"),
            Principal(),
            new FakeTenantContext(null),
            new FakeModeProvider(TammaMode.SaaS),
            Catalog(ctx), Assignments(ctx),
            NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task Subscribe_CustomPlanSlug_Returns422_NotPublic()
    {
        var tenantId = await SeedTenantAsync();
        var customSlug = CustomPlanSlug.New(tenantId);
        await InsertPlanAsync(customSlug, "active", isCustom: true);
        await using var ctx = NewContext();

        var result = await PricingEndpoints.Subscribe(
            new SubscribeRequest(customSlug),
            Principal(),
            new FakeTenantContext(tenantId),
            new FakeModeProvider(TammaMode.SaaS),
            Catalog(ctx), Assignments(ctx),
            NullLoggerFactory.Instance, CancellationToken.None);

        StatusCodeOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Test]
    public async Task Subscribe_Resolves_Tenant_From_Context_Not_Body_Isolation()
    {
        // Two tenants; the caller's context is tenant A. Subscribe assigns to A
        // only — there is no body field that could select tenant B.
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        await using var ctx = NewContext();

        var result = await PricingEndpoints.Subscribe(
            new SubscribeRequest("team"),
            Principal(),
            new FakeTenantContext(tenantA),
            new FakeModeProvider(TammaMode.SaaS),
            Catalog(ctx), Assignments(ctx),
            NullLoggerFactory.Instance, CancellationToken.None);

        var body = ((Microsoft.AspNetCore.Http.HttpResults.Ok<PlanAssignmentResponse>)result).Value!;
        body.TenantId.Should().Be(tenantA);

        // Tenant B is untouched — no active team assignment.
        await using var verify = NewContext();
        (await verify.TenantPlanAssignments.AnyAsync(a => a.TenantId == tenantB))
            .Should().BeFalse("subscribe must not affect another tenant");
    }
}
