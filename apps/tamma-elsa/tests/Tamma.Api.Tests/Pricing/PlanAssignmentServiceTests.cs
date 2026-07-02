using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-4 — <see cref="PlanAssignmentService"/> against a real Postgres
/// testcontainer (so the transactional swap, the partial unique index, and the
/// FK behave like production). Covers version pinning surviving deprecation, the
/// one-active invariant, draft/deprecated/custom guards, direction
/// classification, downgrade warning surfacing, cancel scheduling + boundary
/// activation, and DCB event emission.
/// </summary>
[TestFixture]
public class PlanAssignmentServiceTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tpa_service_test")
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

    private sealed class StubUsageReader : ITenantUsageReader
    {
        private readonly long? _value;
        public StubUsageReader(long? value) => _value = value;
        public Task<long?> GetCurrentUsageAsync(Guid tenantId, EntitlementMetricKey metric, CancellationToken ct = default)
            => Task.FromResult(_value);
    }

    private sealed class FakeModeProvider : ITammaModeProvider
    {
        public FakeModeProvider(TammaMode mode) => Mode = mode;
        public TammaMode Mode { get; }
    }

    private (PlanAssignmentService svc, RecordingPlatformEventPublisher events,
        RecordingPlatformQueuedTaskRepository queue, PricingTestClock clock)
        Build(ControlPlaneDbContext ctx, ITenantUsageReader? usage = null,
              TammaMode mode = TammaMode.SaaS)
    {
        var events = new RecordingPlatformEventPublisher();
        var queue = new RecordingPlatformQueuedTaskRepository();
        var clock = new PricingTestClock();
        var svc = new PlanAssignmentService(
            ctx,
            new PlanCatalogService(ctx, NullLogger<PlanCatalogService>.Instance),
            usage ?? new NullTenantUsageReader(NullLogger<NullTenantUsageReader>.Instance),
            events, queue, new FakeModeProvider(mode), clock,
            NullLogger<PlanAssignmentService>.Instance);
        return (svc, events, queue, clock);
    }

    private async Task<Guid> SeedTenantAsync(Guid? planId = null, string plan = "free")
    {
        await using var ctx = NewContext();
        var id = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = id,
            Name = "Acme",
            Slug = "acme-" + id.ToString("N")[..6],
            Type = "team",
            Plan = plan,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Tenants.Add(tenant);
        ctx.Entry(tenant).Property("Status").CurrentValue = "active";
        ctx.Entry(tenant).Property("PlanId").CurrentValue = planId;
        ctx.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = new byte[] { 1, 2, 3, 4 };
        await ctx.SaveChangesAsync();
        return id;
    }

    private async Task<Guid?> ShadowPlanIdAsync(Guid tenantId)
    {
        await using var ctx = NewContext();
        return await ctx.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => EF.Property<Guid?>(t, "PlanId"))
            .FirstAsync();
    }

    [Test]
    public async Task Assign_Pins_Version_And_Sets_Lockstep_Columns_And_Emits()
    {
        var tenantId = await SeedTenantAsync();

        await using var ctx = NewContext();
        var (svc, events, _, _) = Build(ctx);

        var result = await svc.AssignAsync(
            tenantId, PlansSeeder.TeamPlanId,
            new AssignPlanOptions(Reason: "test", Source: "admin"));

        result.Assignment.PlanId.Should().Be(PlansSeeder.TeamPlanId);
        result.Assignment.PlanVersion.Should().Be(1);
        result.Assignment.Status.Should().Be("active");

        (await ShadowPlanIdAsync(tenantId)).Should().Be(PlansSeeder.TeamPlanId);
        (await NewContext().Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId))
            .Plan.Should().Be("team");

        events.Events.Should().ContainSingle(e => e.Type == PlanAssignmentEventTypes.TenantPlanChanged);
        var tags = events.Events.Single().Tags;
        tags.Should().Contain("\"newPlanVersion\":\"1\"");
        tags.Should().Contain("\"source\":\"admin\"");
    }

    [Test]
    public async Task Assign_Pin_Survives_Plan_Deprecation()
    {
        var tenantId = await SeedTenantAsync();

        // Assign team v1.
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.TeamPlanId, new AssignPlanOptions());
        }

        // Deprecate team (mints v2, flips v1 → deprecated).
        await using (var editCtx = NewContext())
        {
            var editor = new PlanVersionEditor(
                editCtx, new RecordingPlatformEventPublisher(),
                TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            await editor.CreateNewVersionAsync(
                "team", new PlanDraftSpec(DisplayName: "Team v2"),
                new PlanEditorPrincipal("u", "u@x.io"));
        }

        // The pinned assignment still reports v1 — no silent re-price.
        await using var verify = NewContext();
        var (svc2, _, _, _) = Build(verify);
        var active = await svc2.GetActiveAsync(tenantId);
        active!.PlanVersion.Should().Be(1, "the pinned version survives deprecation");
        active.PlanId.Should().Be(PlansSeeder.TeamPlanId);
    }

    [Test]
    public async Task Assign_Twice_Flips_Prior_To_Cancelled_And_Keeps_One_Active()
    {
        var tenantId = await SeedTenantAsync();

        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.TeamPlanId, new AssignPlanOptions());
        }
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.EnterprisePlanId, new AssignPlanOptions());
        }

        await using var verify = NewContext();
        var rows = await verify.TenantPlanAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId).ToListAsync();

        rows.Count(a => a.Status == "active").Should().Be(1, "exactly one active row");
        rows.Single(a => a.Status == "active").PlanId.Should().Be(PlansSeeder.EnterprisePlanId);
        var cancelled = rows.Single(a => a.Status == "cancelled");
        cancelled.PlanId.Should().Be(PlansSeeder.TeamPlanId);
        cancelled.EffectiveTo.Should().NotBeNull("the prior active row's EffectiveTo is stamped");
    }

    [Test]
    public async Task Assign_Same_Version_Is_Lateral_Noop_No_Event()
    {
        var tenantId = await SeedTenantAsync();

        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.TeamPlanId, new AssignPlanOptions());
        }

        await using var ctx2 = NewContext();
        var (svc2, events2, _, _) = Build(ctx2);
        var result = await svc2.AssignAsync(tenantId, PlansSeeder.TeamPlanId, new AssignPlanOptions());

        result.Direction.Should().Be(PlanChangeDirection.Lateral);
        events2.Events.Should().BeEmpty("an idempotent re-assign emits no event");
        (await ctx2.TenantPlanAssignments.CountAsync(a => a.TenantId == tenantId && a.Status == "active"))
            .Should().Be(1);
    }

    [Test]
    public async Task Assign_Draft_Plan_Is_Rejected()
    {
        var tenantId = await SeedTenantAsync();
        var draftId = await InsertPlanAsync(slug: "beta", status: "draft", isCustom: false);

        await using var ctx = NewContext();
        var (svc, _, _, _) = Build(ctx);

        var act = () => svc.AssignAsync(tenantId, draftId, new AssignPlanOptions());
        (await act.Should().ThrowAsync<TammaError>()).Which.Code.Should().Be("PLAN.ASSIGN.PLAN_DRAFT");
    }

    [Test]
    public async Task Assign_Deprecated_Plan_Rejected_Unless_Force()
    {
        var tenantId = await SeedTenantAsync();
        var depId = await InsertPlanAsync(slug: "legacy", status: "deprecated", isCustom: false);

        await using var ctx = NewContext();
        var (svc, _, _, _) = Build(ctx);

        var act = () => svc.AssignAsync(tenantId, depId, new AssignPlanOptions());
        (await act.Should().ThrowAsync<TammaError>()).Which.Code.Should().Be("PLAN.ASSIGN.PLAN_DEPRECATED");

        // With Force it succeeds.
        var forced = await svc.AssignAsync(tenantId, depId, new AssignPlanOptions(Force: true));
        forced.Assignment.PlanId.Should().Be(depId);
    }

    [Test]
    public async Task Assign_Custom_Plan_Misbound_Is_Rejected()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = Guid.NewGuid();
        var customSlug = CustomPlanSlug.New(tenantB);
        var customId = await InsertPlanAsync(slug: customSlug, status: "active", isCustom: true);

        await using var ctx = NewContext();
        var (svc, _, _, _) = Build(ctx);

        var act = () => svc.AssignAsync(tenantA, customId, new AssignPlanOptions());
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("PLAN.ASSIGN.CUSTOM_PLAN_MISBOUND");
    }

    [Test]
    public async Task Assign_Custom_Plan_To_Bound_Tenant_Succeeds()
    {
        var tenantA = await SeedTenantAsync();
        var customSlug = CustomPlanSlug.New(tenantA);
        var customId = await InsertPlanAsync(slug: customSlug, status: "active", isCustom: true);

        await using var ctx = NewContext();
        var (svc, _, _, _) = Build(ctx);

        var result = await svc.AssignAsync(tenantA, customId, new AssignPlanOptions());
        result.Assignment.PlanId.Should().Be(customId);
    }

    [Test]
    public async Task Direction_Is_Upgrade_Then_Downgrade()
    {
        var tenantId = await SeedTenantAsync();

        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            var up = await svc.AssignAsync(tenantId, PlansSeeder.TeamPlanId, new AssignPlanOptions());
            up.Direction.Should().Be(PlanChangeDirection.Upgrade, "free → team is an upgrade");
        }
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            var down = await svc.AssignAsync(tenantId, PlansSeeder.FreePlanId, new AssignPlanOptions());
            down.Direction.Should().Be(PlanChangeDirection.Downgrade, "team → free is a downgrade");
        }
    }

    [Test]
    public async Task Downgrade_OverLimit_Surfaces_Warnings_And_Sets_Event_Tag()
    {
        var tenantId = await SeedTenantAsync();

        // Get onto team first (no warnings on the upgrade).
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.TeamPlanId, new AssignPlanOptions());
        }

        // Downgrade team → free with a usage reader reporting a huge current
        // value ⇒ over EVERY finite free limit.
        await using var ctx2 = NewContext();
        var (svc2, events2, _, _) = Build(ctx2, usage: new StubUsageReader(1_000_000_000));
        var result = await svc2.AssignAsync(tenantId, PlansSeeder.FreePlanId, new AssignPlanOptions());

        result.Warnings.Should().NotBeEmpty("over-limit downgrade flags a warning per finite metric");
        var evt = events2.Events.Single(e => e.Type == PlanAssignmentEventTypes.TenantPlanChanged);
        evt.Tags.Should().Contain("\"entitlementWarnings\":\"true\"");
    }

    [Test]
    public async Task Cancel_PeriodEnd_Schedules_Free_Row_And_Enqueues_Task()
    {
        var tenantId = await SeedTenantAsync();
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.TeamPlanId, new AssignPlanOptions());
        }

        await using var ctx2 = NewContext();
        var (svc2, events2, queue2, _) = Build(ctx2);
        var result = await svc2.CancelAsync(tenantId, new CancelPlanOptions());

        result.ScheduledEffectiveAt.Should().NotBeNull();
        result.Assignment.Status.Should().Be("scheduled");
        result.Assignment.PlanId.Should().Be(PlansSeeder.FreePlanId);

        // The current active team row is still active but now has EffectiveTo set.
        var team = await ctx2.TenantPlanAssignments.AsNoTracking()
            .SingleAsync(a => a.TenantId == tenantId && a.PlanId == PlansSeeder.TeamPlanId);
        team.Status.Should().Be("active");
        team.EffectiveTo.Should().NotBeNull("period-end cancel stamps the boundary");

        queue2.Enqueued.Should().ContainSingle(t =>
            t.Type == Tamma.Api.Services.Provisioning.ActivateScheduledPlanTaskPayload.TaskType);
        events2.Events.Should().ContainSingle(e => e.Type == PlanAssignmentEventTypes.TenantPlanCancelled);
    }

    [Test]
    public async Task Cancel_Immediate_Drops_To_Free_Now()
    {
        var tenantId = await SeedTenantAsync();
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.TeamPlanId, new AssignPlanOptions());
        }

        await using var ctx2 = NewContext();
        var (svc2, events2, _, _) = Build(ctx2);
        var result = await svc2.CancelAsync(tenantId, new CancelPlanOptions(Immediate: true));

        result.Assignment.Status.Should().Be("active");
        result.Assignment.PlanId.Should().Be(PlansSeeder.FreePlanId);
        (await ctx2.TenantPlanAssignments.CountAsync(a => a.TenantId == tenantId && a.Status == "active"))
            .Should().Be(1);
        var evt = events2.Events.Single(e => e.Type == PlanAssignmentEventTypes.TenantPlanCancelled);
        evt.Tags.Should().Contain("\"immediate\":\"true\"");
    }

    [Test]
    public async Task Cancel_When_Already_Free_Is_Noop()
    {
        var tenantId = await SeedTenantAsync();
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.FreePlanId, new AssignPlanOptions());
        }

        await using var ctx2 = NewContext();
        var (svc2, events2, queue2, _) = Build(ctx2);
        var result = await svc2.CancelAsync(tenantId, new CancelPlanOptions());

        result.Direction.Should().Be(PlanChangeDirection.Lateral);
        events2.Events.Should().BeEmpty("cancel on free is a no-op");
        queue2.Enqueued.Should().BeEmpty();
    }

    [Test]
    public async Task ActivateScheduled_Promotes_Due_Row_And_Emits()
    {
        var tenantId = await SeedTenantAsync();
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.TeamPlanId, new AssignPlanOptions());
        }

        Guid scheduledId;
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            var cancel = await svc.CancelAsync(tenantId, new CancelPlanOptions());
            scheduledId = cancel.Assignment.Id;
        }

        await using var ctx2 = NewContext();
        var (svc2, events2, _, _) = Build(ctx2);
        var result = await svc2.ActivateScheduledAsync(tenantId, scheduledId);

        result.Should().NotBeNull();
        result!.Assignment.Status.Should().Be("active");
        result.Assignment.PlanId.Should().Be(PlansSeeder.FreePlanId);

        var rows = await ctx2.TenantPlanAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId).ToListAsync();
        rows.Count(a => a.Status == "active").Should().Be(1);
        rows.Single(a => a.Status == "active").PlanId.Should().Be(PlansSeeder.FreePlanId);
        rows.Single(a => a.PlanId == PlansSeeder.TeamPlanId).Status.Should().Be("cancelled");

        var evt = events2.Events.Single(e => e.Type == PlanAssignmentEventTypes.TenantPlanChanged);
        evt.Tags.Should().Contain("\"source\":\"scheduled-activation\"");

        // Idempotent re-run — no-op.
        var again = await svc2.ActivateScheduledAsync(tenantId, scheduledId);
        again!.Direction.Should().Be(PlanChangeDirection.Lateral);
    }

    [Test]
    public async Task ReAssign_After_PeriodEnd_Cancel_Voids_Scheduled_And_Boundary_Does_Not_Revert()
    {
        // Finding 1 — a scheduled period-end downgrade must be reconciled with a
        // later re-assign. team → period-end cancel (schedules a free downgrade) →
        // re-assign enterprise → at the boundary ActivateScheduledAsync must NOT
        // drop the upgraded tenant to free.
        var tenantId = await SeedTenantAsync();

        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.TeamPlanId, new AssignPlanOptions());
        }

        Guid scheduledId;
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            var cancel = await svc.CancelAsync(tenantId, new CancelPlanOptions());
            scheduledId = cancel.Assignment.Id;
        }

        // Re-assign enterprise BEFORE the boundary — this must void the pending
        // scheduled free downgrade in the same swap.
        await using (var ctx = NewContext())
        {
            var (svc, _, _, _) = Build(ctx);
            await svc.AssignAsync(tenantId, PlansSeeder.EnterprisePlanId, new AssignPlanOptions());
        }

        // The scheduled free row is voided (cancelled), not left pending.
        await using (var verify = NewContext())
        {
            var scheduledRow = await verify.TenantPlanAssignments.AsNoTracking()
                .SingleAsync(a => a.Id == scheduledId);
            scheduledRow.Status.Should().Be("cancelled",
                "a re-assign voids the stale scheduled downgrade in the same transaction");
        }

        // Boundary fires: activating the (now voided) scheduled row is a no-op.
        await using var ctx2 = NewContext();
        var (svc2, _, _, _) = Build(ctx2);
        var activated = await svc2.ActivateScheduledAsync(tenantId, scheduledId);
        activated.Should().BeNull("the voided scheduled row is not promoted");

        var rows = await ctx2.TenantPlanAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId).ToListAsync();
        rows.Count(a => a.Status == "active").Should().Be(1, "exactly one active plan — no gap");
        rows.Single(a => a.Status == "active").PlanId.Should().Be(PlansSeeder.EnterprisePlanId,
            "the re-assigned enterprise plan survives — the tenant is NOT reverted to free");
        rows.Where(a => a.PlanId == PlansSeeder.FreePlanId)
            .Should().OnlyContain(a => a.Status == "cancelled",
                "the scheduled free row is voided, never promoted");
    }

    /// <summary>Inserts a bare plan version row (no children) for guard tests.</summary>
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
            MonthlyPriceUsd = 0m,
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
}
