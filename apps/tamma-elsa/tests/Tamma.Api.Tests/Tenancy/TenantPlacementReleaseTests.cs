using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.Tenancy;

/// <summary>
/// Unified-tenancy Phase 2 Task 5 — placement release on the delete
/// path. <see cref="TenantPlacementShadow.ReleaseAsync"/> is the helper
/// both terminal delete activities (<c>EmitDeletedSuccessActivity</c> and
/// <c>SoftDeleteTenantRowActivity</c>) call BEFORE their SaveChanges so
/// the pool row's <c>TenantCount</c> decrement + the
/// <c>DatabaseId</c>/<c>SchemaName</c> shadow-prop nulling land in the
/// SAME transaction as the soft-delete + envelope null.
///
/// <para>Harness mirrors <see cref="TenantPlacementServiceTests"/>: EF
/// in-memory <see cref="ControlPlaneDbContext"/> per test — the release
/// is pure control-plane bookkeeping.</para>
/// </summary>
[TestFixture]
public class TenantPlacementReleaseTests
{
    private static ControlPlaneDbContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new ControlPlaneDbContext(options);
    }

    private static async Task<(Guid TenantId, Guid PoolRowId)> SeedPlacedTenantAsync(
        string dbName, int tenantCount = 1)
    {
        var tenantId = Guid.NewGuid();
        var poolRowId = Guid.NewGuid();
        await using var ctx = CreateContext(dbName);

        ctx.TenantDatabases.Add(new TenantDatabase
        {
            Id = poolRowId,
            Label = $"pool-{poolRowId:N}"[..20],
            Host = "db.internal",
            Port = 5432,
            AdminConnectionStringEncrypted = [1, 2, 3],
            PlacementClass = "shared",
            TierEligibility = ["free", "team"],
            TenantCount = tenantCount,
            Status = "active",
            KekVersion = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Release Test Tenant",
            Slug = $"release-{tenantId:N}"[..20],
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Tenants.Add(tenant);
        var entry = ctx.Entry(tenant);
        entry.Property<Guid?>("DatabaseId").CurrentValue = poolRowId;
        entry.Property<string?>("SchemaName").CurrentValue = TenantNaming.SchemaName(tenantId);
        await ctx.SaveChangesAsync();

        return (tenantId, poolRowId);
    }

    [Test]
    public async Task Release_DecrementsPoolCount_AndNullsShadowProps()
    {
        var dbName = nameof(Release_DecrementsPoolCount_AndNullsShadowProps);
        var (tenantId, poolRowId) = await SeedPlacedTenantAsync(dbName, tenantCount: 3);

        await using (var ctx = CreateContext(dbName))
        {
            var tenant = await ctx.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
            var released = await TenantPlacementShadow.ReleaseAsync(
                ctx, tenant, null, CancellationToken.None);
            released.Should().BeTrue();
            await ctx.SaveChangesAsync();
        }

        await using var verify = CreateContext(dbName);
        var verifyTenant = await verify.Tenants.IgnoreQueryFilters()
            .SingleAsync(t => t.Id == tenantId);
        var entry = verify.Entry(verifyTenant);
        entry.Property<Guid?>("DatabaseId").CurrentValue.Should().BeNull(
            "the released tenant no longer occupies a pool slot");
        entry.Property<string?>("SchemaName").CurrentValue.Should().BeNull();

        var poolRow = await verify.TenantDatabases.SingleAsync(d => d.Id == poolRowId);
        poolRow.TenantCount.Should().Be(2, "release decrements the pool row's TenantCount");
    }

    [Test]
    public async Task Release_FloorsTenantCountAtZero()
    {
        // A drifted counter (e.g. operator repair) must never go negative.
        var dbName = nameof(Release_FloorsTenantCountAtZero);
        var (tenantId, poolRowId) = await SeedPlacedTenantAsync(dbName, tenantCount: 0);

        await using (var ctx = CreateContext(dbName))
        {
            var tenant = await ctx.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
            (await TenantPlacementShadow.ReleaseAsync(ctx, tenant, null, CancellationToken.None))
                .Should().BeTrue();
            await ctx.SaveChangesAsync();
        }

        await using var verify = CreateContext(dbName);
        (await verify.TenantDatabases.SingleAsync(d => d.Id == poolRowId))
            .TenantCount.Should().Be(0, "TenantCount floors at 0");
    }

    [Test]
    public async Task Release_UnplacedTenant_IsNoOp()
    {
        var dbName = nameof(Release_UnplacedTenant_IsNoOp);
        var tenantId = Guid.NewGuid();
        await using (var ctx = CreateContext(dbName))
        {
            ctx.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Unplaced Tenant",
                Slug = $"unplaced-{tenantId:N}"[..20],
                Plan = "free",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var release = CreateContext(dbName);
        var tenant = await release.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);

        (await TenantPlacementShadow.ReleaseAsync(release, tenant, null, CancellationToken.None))
            .Should().BeFalse("a tenant that was never placed has nothing to release");
    }

    [Test]
    public async Task Release_IsIdempotent_SecondCallDoesNotDoubleDecrement()
    {
        // A replayed terminal step (Elsa retry of EmitDeletedSuccess /
        // SoftDeleteTenantRow) must not decrement the pool slot twice.
        var dbName = nameof(Release_IsIdempotent_SecondCallDoesNotDoubleDecrement);
        var (tenantId, poolRowId) = await SeedPlacedTenantAsync(dbName, tenantCount: 2);

        await using (var ctx = CreateContext(dbName))
        {
            var tenant = await ctx.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
            (await TenantPlacementShadow.ReleaseAsync(ctx, tenant, null, CancellationToken.None))
                .Should().BeTrue();
            await ctx.SaveChangesAsync();
        }

        await using (var replay = CreateContext(dbName))
        {
            var tenant = await replay.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
            (await TenantPlacementShadow.ReleaseAsync(replay, tenant, null, CancellationToken.None))
                .Should().BeFalse("the shadow props are already null — nothing to release");
            await replay.SaveChangesAsync();
        }

        await using var verify = CreateContext(dbName);
        (await verify.TenantDatabases.SingleAsync(d => d.Id == poolRowId))
            .TenantCount.Should().Be(1, "exactly one decrement across the replayed releases");
    }

    [Test]
    public async Task Release_MissingPoolRow_StillNullsShadowProps()
    {
        // FK Restrict makes this near-impossible, but a missing registry
        // row must not block the tenant's deletion.
        var dbName = nameof(Release_MissingPoolRow_StillNullsShadowProps);
        var tenantId = Guid.NewGuid();
        await using (var ctx = CreateContext(dbName))
        {
            var tenant = new Tenant
            {
                Id = tenantId,
                Name = "Orphan Placement Tenant",
                Slug = $"orphan-{tenantId:N}"[..20],
                Plan = "free",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.Tenants.Add(tenant);
            var entry = ctx.Entry(tenant);
            entry.Property<Guid?>("DatabaseId").CurrentValue = Guid.NewGuid(); // no such pool row
            entry.Property<string?>("SchemaName").CurrentValue = TenantNaming.SchemaName(tenantId);
            await ctx.SaveChangesAsync();
        }

        await using (var release = CreateContext(dbName))
        {
            var tenant = await release.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
            (await TenantPlacementShadow.ReleaseAsync(release, tenant, null, CancellationToken.None))
                .Should().BeTrue();
            await release.SaveChangesAsync();
        }

        await using var verify = CreateContext(dbName);
        var verifyTenant = await verify.Tenants.IgnoreQueryFilters()
            .SingleAsync(t => t.Id == tenantId);
        verify.Entry(verifyTenant).Property<Guid?>("DatabaseId").CurrentValue.Should().BeNull();
        verify.Entry(verifyTenant).Property<string?>("SchemaName").CurrentValue.Should().BeNull();
    }
}
