using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Data.Abstractions;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Tenancy;

/// <summary>
/// Unified-tenancy Phase 2 Task 2 — <see cref="TenantPlacementService"/>
/// assigns a tenant to a <c>tenant_databases</c> pool row by plan tier
/// (plans.PlacementPolicy) and stamps the <c>SchemaName</c>/<c>DatabaseId</c>
/// shadow columns on the tenant.
///
/// <para>Harness mirrors the control-plane side of
/// <see cref="TenantDatabasePoolTests"/>: EF in-memory
/// <see cref="ControlPlaneDbContext"/> per test. Placement is pure
/// control-plane bookkeeping — it never opens a connection to the target
/// cluster — so no real Postgres is needed, and
/// <c>TierEligibility.Contains</c> evaluates as plain LINQ-to-objects under
/// the in-memory provider (against Npgsql it translates to a
/// <c>text[]</c> containment predicate). Plans come from the production
/// <see cref="PlansSeeder"/> (free/team=shared, enterprise=dedicated); pool
/// rows are inserted per test case.</para>
/// </summary>
[TestFixture]
public class TenantPlacementServiceTests
{
    private static ControlPlaneDbContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new ControlPlaneDbContext(options);
    }

    private static TenantPlacementService CreateService(string dbName) => new(
        new InMemoryCpFactory(dbName),
        NullLogger<TenantPlacementService>.Instance);

    private static async Task<Guid> SeedTenantAsync(string dbName, string plan)
    {
        var tenantId = Guid.NewGuid();
        await using var ctx = CreateContext(dbName);
        await PlansSeeder.SeedAsync(ctx);
        ctx.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Placement Test Tenant",
            Slug = $"placement-{tenantId:N}"[..20],
            Plan = plan,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return tenantId;
    }

    private static TenantDatabase PoolRow(
        string label,
        string placementClass = "shared",
        string[]? tiers = null,
        int? capacity = null,
        int tenantCount = 0,
        string status = "active",
        DateTime? createdAt = null)
    {
        var ts = createdAt ?? DateTime.UtcNow;
        return new TenantDatabase
        {
            Id = Guid.NewGuid(),
            Label = label,
            Host = "db.internal",
            Port = 5432,
            AdminConnectionStringEncrypted = [1, 2, 3],
            PlacementClass = placementClass,
            TierEligibility = tiers ?? ["free", "team", "enterprise"],
            TenantCapacity = capacity,
            TenantCount = tenantCount,
            Status = status,
            KekVersion = 1,
            CreatedAt = ts,
            UpdatedAt = ts,
        };
    }

    private static async Task AddPoolRowsAsync(string dbName, params TenantDatabase[] rows)
    {
        await using var ctx = CreateContext(dbName);
        ctx.TenantDatabases.AddRange(rows);
        await ctx.SaveChangesAsync();
    }

    [Test]
    public async Task Assign_FreeTenant_LandsOnSharedRow_StampsSchemaAndDatabase()
    {
        var dbName = nameof(Assign_FreeTenant_LandsOnSharedRow_StampsSchemaAndDatabase);
        var tenantId = await SeedTenantAsync(dbName, plan: "free");
        var row = PoolRow("central");
        await AddPoolRowsAsync(dbName, row);

        var placement = await CreateService(dbName).AssignAsync(tenantId);

        placement.DatabaseId.Should().Be(row.Id);
        placement.SchemaName.Should().Be(TenantNaming.SchemaName(tenantId),
            "the schema name is the canonical t_<hex> derived from the tenant id");

        await using var verify = CreateContext(dbName);
        var tenant = await verify.Tenants.SingleAsync(t => t.Id == tenantId);
        var entry = verify.Entry(tenant);
        entry.Property<string?>("SchemaName").CurrentValue
            .Should().Be(TenantNaming.SchemaName(tenantId),
                "placement must stamp the SchemaName shadow column");
        entry.Property<Guid?>("DatabaseId").CurrentValue.Should().Be(row.Id,
            "placement must stamp the DatabaseId shadow column");

        var poolRow = await verify.TenantDatabases.SingleAsync(d => d.Id == row.Id);
        poolRow.TenantCount.Should().Be(1, "placement increments the pool row's TenantCount");
    }

    [Test]
    public async Task Assign_IsIdempotent()
    {
        var dbName = nameof(Assign_IsIdempotent);
        var tenantId = await SeedTenantAsync(dbName, plan: "free");
        var row = PoolRow("central");
        await AddPoolRowsAsync(dbName, row);
        var service = CreateService(dbName);

        var first = await service.AssignAsync(tenantId);
        var second = await service.AssignAsync(tenantId);

        second.Should().Be(first,
            "an already-placed tenant returns its existing placement unchanged");

        await using var verify = CreateContext(dbName);
        var poolRow = await verify.TenantDatabases.SingleAsync(d => d.Id == row.Id);
        poolRow.TenantCount.Should().Be(1,
            "a second AssignAsync must not double-count the tenant");
    }

    [Test]
    public async Task Assign_EnterpriseTenant_NoDedicatedRow_Throws()
    {
        var dbName = nameof(Assign_EnterpriseTenant_NoDedicatedRow_Throws);
        var tenantId = await SeedTenantAsync(dbName, plan: "enterprise");
        // Only the central bootstrap-style row exists: shared, all tiers.
        // Enterprise's PlacementPolicy is 'dedicated' — shared rows never
        // qualify; the operator must add a dedicated row (Phase 4 CRUD).
        await AddPoolRowsAsync(dbName, PoolRow("central"));

        var act = async () => await CreateService(dbName).AssignAsync(tenantId);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "no eligible pool row must fail loudly, never default silently"))
            .Where(ex => ex.Message.Contains("enterprise") && ex.Message.Contains("dedicated"),
                "the error must name the tier and the placement policy so the " +
                "operator knows which kind of pool row to add");
    }

    [Test]
    public async Task Assign_SkipsFullAndNonActiveRows()
    {
        var dbName = nameof(Assign_SkipsFullAndNonActiveRows);
        var tenantId = await SeedTenantAsync(dbName, plan: "free");
        var t0 = DateTime.UtcNow;
        // Both ineligible rows would win on TenantCount-ascending ordering
        // if the filters failed: 'draining' has count 0, 'full' has count 1,
        // while the only eligible row already hosts 5 tenants.
        var draining = PoolRow("draining-row", tenantCount: 0, status: "draining",
            createdAt: t0.AddDays(-2));
        var full = PoolRow("full-row", capacity: 1, tenantCount: 1,
            createdAt: t0.AddDays(-1));
        var eligible = PoolRow("eligible-row", tenantCount: 5, createdAt: t0);
        await AddPoolRowsAsync(dbName, draining, full, eligible);

        var placement = await CreateService(dbName).AssignAsync(tenantId);

        placement.DatabaseId.Should().Be(eligible.Id,
            "full (TenantCount == TenantCapacity) and non-active rows must be skipped");

        await using var verify = CreateContext(dbName);
        (await verify.TenantDatabases.SingleAsync(d => d.Id == eligible.Id))
            .TenantCount.Should().Be(6);
        (await verify.TenantDatabases.SingleAsync(d => d.Id == full.Id))
            .TenantCount.Should().Be(1, "skipped rows are untouched");
        (await verify.TenantDatabases.SingleAsync(d => d.Id == draining.Id))
            .TenantCount.Should().Be(0, "skipped rows are untouched");
    }

    [Test]
    public async Task Assign_SoftDeletedTenant_Throws()
    {
        var dbName = nameof(Assign_SoftDeletedTenant_Throws);
        var tenantId = Guid.NewGuid();
        await using var ctx = CreateContext(dbName);
        await PlansSeeder.SeedAsync(ctx);
        ctx.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Soft-Deleted Tenant",
            Slug = $"deleted-{tenantId:N}"[..20],
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        await AddPoolRowsAsync(dbName, PoolRow("central"));

        var act = async () => await CreateService(dbName).AssignAsync(tenantId);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "placement of a soft-deleted tenant must be rejected"))
            .Where(ex => ex.Message.Contains("soft-deleted"),
                "the error must mention 'soft-deleted' so the caller knows why placement was refused");
    }

    [Test]
    public async Task Assign_MultiVersionSlug_ResolvesActiveVersionsPlacementPolicy()
    {
        // Story 34-1 regression — a slug is now a version chain (a deprecated
        // v1 + an active v2). The placement lookup must pin Status=="active":
        // the live v2 says PlacementPolicy="shared", while the deprecated v1
        // (stale) says "dedicated". With only a SHARED pool row available,
        // placement succeeds ONLY if the lookup resolves v2's policy. Before
        // the fix the slug lookup had no Status filter and could pick v1,
        // failing placement against the shared row.
        var dbName = nameof(Assign_MultiVersionSlug_ResolvesActiveVersionsPlacementPolicy);
        var tenantId = Guid.NewGuid();

        await using (var seed = CreateContext(dbName))
        {
            await PlansSeeder.SeedAsync(seed);

            var v1Id = Guid.NewGuid();
            var v2Id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            // v1 (deprecated) carries the STALE policy.
            seed.Plans.Add(new Plan
            {
                Id = v1Id, Slug = "versioned", DisplayName = "Versioned v1",
                Version = 1, Status = "deprecated", BillingInterval = "monthly",
                MonthlyPriceUsd = 10m, PlacementPolicy = "dedicated",
                CreatedAt = now, UpdatedAt = now,
            });
            // v2 (active) carries the LIVE policy — the one placement must use.
            seed.Plans.Add(new Plan
            {
                Id = v2Id, Slug = "versioned", DisplayName = "Versioned v2",
                Version = 2, Status = "active", BillingInterval = "monthly",
                MonthlyPriceUsd = 20m, PlacementPolicy = "shared",
                SupersedesPlanId = v1Id, CreatedAt = now, UpdatedAt = now,
            });

            seed.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Versioned Plan Tenant",
                Slug = $"versioned-{tenantId:N}"[..20],
                Plan = "versioned",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await seed.SaveChangesAsync();
        }

        var sharedRow = PoolRow("shared-central", placementClass: "shared",
            tiers: ["versioned", "free", "team", "enterprise"]);
        await AddPoolRowsAsync(dbName, sharedRow);

        var placement = await CreateService(dbName).AssignAsync(tenantId);

        placement.DatabaseId.Should().Be(sharedRow.Id,
            "placement must resolve the ACTIVE v2 (PlacementPolicy='shared') and land on "
            + "the shared pool row — not the deprecated v1 ('dedicated')");
    }

    [Test]
    public async Task Assign_CustomPlanTenant_ResolvesPinnedPlanId_NotStaleSlug()
    {
        // Finding 2 (Story 34-4) — a custom-plan tenant has its version-pinned
        // Tenant.PlanId shadow FK set to a CUSTOM plan while the legacy Tenant.Plan
        // slug stays stale/non-canonical (a custom slug can't sit in the
        // ck_tenants_plan-constrained column). Placement must resolve the
        // PlacementPolicy/tier from PlanId, not the stale slug. Here the stale slug
        // is "team" (shared) but the pinned custom plan is 'dedicated'; only a
        // dedicated pool row (eligible for the custom tier) is available, so
        // placement SUCCEEDS only if it resolves the pinned custom plan — resolving
        // the stale "team" slug ('shared') would find no eligible row and throw.
        var dbName = nameof(Assign_CustomPlanTenant_ResolvesPinnedPlanId_NotStaleSlug);
        var tenantId = Guid.NewGuid();
        var customPlanId = Guid.NewGuid();
        var customSlug = $"custom-{tenantId:N}"[..20];

        await using (var seed = CreateContext(dbName))
        {
            await PlansSeeder.SeedAsync(seed);
            var now = DateTime.UtcNow;
            seed.Plans.Add(new Plan
            {
                Id = customPlanId,
                Slug = customSlug,
                DisplayName = "Bespoke Enterprise",
                Version = 1,
                Status = "active",
                IsCustom = true,
                BillingInterval = "monthly",
                MonthlyPriceUsd = 999m,
                PlacementPolicy = "dedicated",
                CreatedAt = now,
                UpdatedAt = now,
            });

            var tenant = new Tenant
            {
                Id = tenantId,
                Name = "Custom Plan Tenant",
                Slug = $"custom-t-{tenantId:N}"[..20],
                Plan = "team", // STALE legacy slug (shared) — must be IGNORED
                CreatedAt = now,
                UpdatedAt = now,
            };
            seed.Tenants.Add(tenant);
            seed.Entry(tenant).Property<Guid?>("PlanId").CurrentValue = customPlanId;
            await seed.SaveChangesAsync();
        }

        // Only a DEDICATED row (eligible for the custom tier) exists. A stale-slug
        // ("team" → 'shared') resolution would find no eligible row and throw.
        var dedicated = PoolRow("dedicated-1", placementClass: "dedicated",
            tiers: [customSlug]);
        await AddPoolRowsAsync(dbName, dedicated);

        var placement = await CreateService(dbName).AssignAsync(tenantId);

        placement.DatabaseId.Should().Be(dedicated.Id,
            "placement must resolve the version-pinned custom plan ('dedicated') from "
            + "Tenant.PlanId, not the stale legacy slug ('team' → 'shared')");
    }

    [Test]
    public async Task Assign_CorruptState_OnePropSet_ReStamps()
    {
        var dbName = nameof(Assign_CorruptState_OnePropSet_ReStamps);
        var tenantId = Guid.NewGuid();

        // Seed tenant with only SchemaName stamped (DatabaseId left null) —
        // this represents a half-stamped / corrupt row.
        await using var seed = CreateContext(dbName);
        await PlansSeeder.SeedAsync(seed);
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Corrupt State Tenant",
            Slug = $"corrupt-{tenantId:N}"[..20],
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        seed.Tenants.Add(tenant);
        await seed.SaveChangesAsync();

        // Write only SchemaName shadow column, leave DatabaseId null.
        var seedEntry = seed.Entry(tenant);
        seedEntry.Property<string?>("SchemaName").CurrentValue = TenantNaming.SchemaName(tenantId);
        await seed.SaveChangesAsync();

        var row = PoolRow("central");
        await AddPoolRowsAsync(dbName, row);

        var placement = await CreateService(dbName).AssignAsync(tenantId);

        placement.DatabaseId.Should().Be(row.Id,
            "a half-stamped (corrupt) tenant must be treated as unplaced and re-stamped");
        placement.SchemaName.Should().Be(TenantNaming.SchemaName(tenantId));

        await using var verify = CreateContext(dbName);
        var verifyTenant = await verify.Tenants.SingleAsync(t => t.Id == tenantId);
        var verifyEntry = verify.Entry(verifyTenant);
        verifyEntry.Property<string?>("SchemaName").CurrentValue
            .Should().Be(TenantNaming.SchemaName(tenantId),
                "re-stamp must set SchemaName");
        verifyEntry.Property<Guid?>("DatabaseId").CurrentValue.Should().Be(row.Id,
            "re-stamp must set DatabaseId");

        var poolRow = await verify.TenantDatabases.SingleAsync(d => d.Id == row.Id);
        poolRow.TenantCount.Should().Be(1, "re-stamp must increment TenantCount");
    }

    private sealed class InMemoryCpFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly string _dbName;
        public InMemoryCpFactory(string dbName) => _dbName = dbName;

        public ControlPlaneDbContext CreateDbContext() => CreateContext(_dbName);

        public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }
}
