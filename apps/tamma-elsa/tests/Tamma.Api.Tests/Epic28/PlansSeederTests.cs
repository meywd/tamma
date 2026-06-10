using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-1 / 28-2 — verifies the <see cref="PlansSeeder"/> inserts
/// the three default plans deterministically and is a no-op on re-run.
///
/// Uses an EF in-memory provider — the seeder operation is a basic
/// AddRange + SaveChanges, so the cheaper provider catches all the
/// behaviour we care about (idempotency + stable IDs).
/// </summary>
[TestFixture]
public class PlansSeederTests
{
    private static ControlPlaneDbContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        return new ControlPlaneDbContext(options);
    }

    [Test]
    public async Task SeedAsync_OnEmptyTable_Inserts_Three_Plans_With_Stable_Ids()
    {
        var dbName = nameof(SeedAsync_OnEmptyTable_Inserts_Three_Plans_With_Stable_Ids);
        await using var ctx = CreateContext(dbName);

        await PlansSeeder.SeedAsync(ctx);

        var rows = await ctx.Plans.AsNoTracking().OrderBy(p => p.Slug).ToListAsync();
        rows.Should().HaveCount(3);

        rows.Select(p => p.Slug).Should().BeEquivalentTo(new[] { "enterprise", "free", "team" });
        rows.Select(p => p.Id).Should().BeEquivalentTo(new[]
        {
            PlansSeeder.EnterprisePlanId,
            PlansSeeder.FreePlanId,
            PlansSeeder.TeamPlanId,
        });
    }

    [Test]
    public async Task SeedAsync_OnReRun_Is_NoOp()
    {
        var dbName = nameof(SeedAsync_OnReRun_Is_NoOp);
        await using var first = CreateContext(dbName);
        await PlansSeeder.SeedAsync(first);

        await using var second = CreateContext(dbName);
        await PlansSeeder.SeedAsync(second);

        var rows = await second.Plans.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(3, "second SeedAsync must short-circuit on EXISTS");
    }

    [Test]
    public void Plan_Ids_Are_Distinct_And_Stable()
    {
        // Stability — these IDs are seed FKs that ship in production data.
        // Anything that changes them is a breaking change.
        PlansSeeder.FreePlanId.Should().NotBe(PlansSeeder.TeamPlanId);
        PlansSeeder.TeamPlanId.Should().NotBe(PlansSeeder.EnterprisePlanId);
        PlansSeeder.FreePlanId.Should().NotBe(PlansSeeder.EnterprisePlanId);
    }

    /// <summary>
    /// Unified-tenancy plan 2026-06-09 §2.3, decision 2 — verifies that each
    /// tier gets the correct placement policy: free/team go to the shared
    /// pool; enterprise gets a dedicated single-tenant DB.
    /// </summary>
    [Test]
    public async Task SeedAsync_SetsPlacementPolicyPerTier()
    {
        var dbName = nameof(SeedAsync_SetsPlacementPolicyPerTier);
        await using var ctx = CreateContext(dbName);

        await PlansSeeder.SeedAsync(ctx);

        var bySlug = await ctx.Plans.AsNoTracking().ToDictionaryAsync(p => p.Slug);
        bySlug["free"].PlacementPolicy.Should().Be("shared");
        bySlug["team"].PlacementPolicy.Should().Be("shared");
        bySlug["enterprise"].PlacementPolicy.Should().Be("dedicated");
    }
}
