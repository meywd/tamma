using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-1 (AC9, AC13) — the extended <see cref="PlansSeeder"/> seeds typed
/// feature/entitlement/price children for free/team/enterprise with
/// deterministic UUIDs, insert-missing-only per row, never reverting admin
/// edits. Uses the EF in-memory provider (the seed is AddRange + per-row
/// existence checks — the cheap provider catches idempotency + no-revert; the
/// DB-level CHECK/partial-unique invariants are covered by the Postgres suite).
/// </summary>
[TestFixture]
public class PlansSeederStructuredTests
{
    private static ControlPlaneDbContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Test]
    public async Task SeedAsync_Inserts_Structured_Children_For_All_Three_Slugs()
    {
        var dbName = nameof(SeedAsync_Inserts_Structured_Children_For_All_Three_Slugs);
        await using var ctx = CreateContext(dbName);

        await PlansSeeder.SeedAsync(ctx);

        var plans = await ctx.Plans.AsNoTracking().ToListAsync();
        plans.Should().HaveCount(3);
        plans.Should().OnlyContain(p => p.Version == 1 && p.Status == "active",
            "seeded v1 rows are the active version");

        // Each slug gets features, entitlements, and both pricing-mode rows.
        foreach (var planId in new[] { PlansSeeder.FreePlanId, PlansSeeder.TeamPlanId, PlansSeeder.EnterprisePlanId })
        {
            (await ctx.PlanFeatures.AsNoTracking().CountAsync(f => f.PlanId == planId))
                .Should().BeGreaterThan(0);
            (await ctx.PlanEntitlements.AsNoTracking().CountAsync(e => e.PlanId == planId))
                .Should().BeGreaterThan(0);

            var modes = await ctx.PlanPrices.AsNoTracking()
                .Where(p => p.PlanId == planId)
                .Select(p => p.PricingMode)
                .ToListAsync();
            modes.Should().BeEquivalentTo(new[] { "platform_provided", "byok" },
                "every plan version stores a distinct price row per pricing mode");
        }
    }

    [Test]
    public async Task SeedAsync_MetricKeys_RoundTrip_Through_Converter()
    {
        var dbName = nameof(SeedAsync_MetricKeys_RoundTrip_Through_Converter);
        await using var ctx = CreateContext(dbName);

        await PlansSeeder.SeedAsync(ctx);

        var ents = await ctx.PlanEntitlements.AsNoTracking()
            .Where(e => e.PlanId == PlansSeeder.EnterprisePlanId)
            .ToListAsync();

        ents.Select(e => e.MetricKey).Should().Contain(EntitlementMetricKey.LlmTokens);
        // Enterprise stores unlimited (NULL) seat/agent/repo limits.
        ents.Should().Contain(e => e.MetricKey == EntitlementMetricKey.Seats && e.LimitValue == null);
    }

    [Test]
    public async Task SeedAsync_SecondRun_Is_NoOp()
    {
        var dbName = nameof(SeedAsync_SecondRun_Is_NoOp);

        await using (var first = CreateContext(dbName))
        {
            await PlansSeeder.SeedAsync(first);
        }

        int featuresAfterFirst, entitlementsAfterFirst, pricesAfterFirst;
        await using (var read = CreateContext(dbName))
        {
            featuresAfterFirst = await read.PlanFeatures.CountAsync();
            entitlementsAfterFirst = await read.PlanEntitlements.CountAsync();
            pricesAfterFirst = await read.PlanPrices.CountAsync();
        }

        await using (var second = CreateContext(dbName))
        {
            await PlansSeeder.SeedAsync(second);
        }

        await using var verify = CreateContext(dbName);
        (await verify.Plans.CountAsync()).Should().Be(3, "no duplicate plan rows");
        (await verify.PlanFeatures.CountAsync()).Should().Be(featuresAfterFirst);
        (await verify.PlanEntitlements.CountAsync()).Should().Be(entitlementsAfterFirst);
        (await verify.PlanPrices.CountAsync()).Should().Be(pricesAfterFirst);
    }

    [Test]
    public async Task SeedAsync_Does_Not_Revert_An_Edited_Row()
    {
        var dbName = nameof(SeedAsync_Does_Not_Revert_An_Edited_Row);

        await using (var first = CreateContext(dbName))
        {
            await PlansSeeder.SeedAsync(first);
        }

        // Admin edits the free plan's display name (a v2 draft would normally
        // do this; here we mutate the seeded row directly to model an admin
        // edit the seeder must NOT clobber).
        await using (var edit = CreateContext(dbName))
        {
            var free = await edit.Plans.FirstAsync(p => p.Id == PlansSeeder.FreePlanId);
            free.DisplayName = "Free (admin-renamed)";
            await edit.SaveChangesAsync();
        }

        await using (var reseed = CreateContext(dbName))
        {
            await PlansSeeder.SeedAsync(reseed);
        }

        await using var verify = CreateContext(dbName);
        var reloaded = await verify.Plans.AsNoTracking()
            .FirstAsync(p => p.Id == PlansSeeder.FreePlanId);
        reloaded.DisplayName.Should().Be("Free (admin-renamed)",
            "insert-missing-only seeder must never revert an admin-edited row");
    }

    [Test]
    public async Task SeedAsync_Backfills_Children_Onto_A_BarePlanRow()
    {
        // Models a DB that already had the Story 28-1 bare v1 plan rows but no
        // structured children — the seeder must backfill children without
        // duplicating the plan.
        var dbName = nameof(SeedAsync_Backfills_Children_Onto_A_BarePlanRow);

        await using (var bare = CreateContext(dbName))
        {
            bare.Plans.Add(new Plan
            {
                Id = PlansSeeder.FreePlanId,
                Slug = "free",
                DisplayName = "Free",
                Version = 1,
                Status = "active",
                MonthlyPriceUsd = 0m,
                PlacementPolicy = "shared",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await bare.SaveChangesAsync();
        }

        await using (var seed = CreateContext(dbName))
        {
            await PlansSeeder.SeedAsync(seed);
        }

        await using var verify = CreateContext(dbName);
        (await verify.Plans.CountAsync(p => p.Id == PlansSeeder.FreePlanId)).Should().Be(1,
            "no duplicate plan row");
        (await verify.PlanFeatures.CountAsync(f => f.PlanId == PlansSeeder.FreePlanId))
            .Should().BeGreaterThan(0, "children backfilled onto the pre-existing bare plan row");
    }
}
