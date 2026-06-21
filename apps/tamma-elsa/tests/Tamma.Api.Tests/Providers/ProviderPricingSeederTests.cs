using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// Story 34-11 (AC8, AC12) — <see cref="ProviderPricingSeeder"/> ports the
/// frozen rate sheet into providers + provider_model_prices as v1 seed rows,
/// insert-missing-only, with deterministic UUIDv7-shaped ids. Uses the EF
/// in-memory provider (the seed is per-row existence checks — the cheap provider
/// catches idempotency + no-revert; the DB-level CHECK / partial-unique
/// invariants + the parity/window behaviours are covered by the Postgres suite).
/// </summary>
[TestFixture]
public class ProviderPricingSeederTests
{
    private static ControlPlaneDbContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Test]
    public async Task SeedAsync_Inserts_All_Frozen_Providers_With_Correct_AuthModel()
    {
        var ctx = CreateContext(nameof(SeedAsync_Inserts_All_Frozen_Providers_With_Correct_AuthModel));

        var inserted = await ProviderPricingSeeder.SeedAsync(ctx);
        inserted.Should().BeGreaterThan(0);

        var providers = await ctx.Providers.AsNoTracking().ToListAsync();
        providers.Select(p => p.Key).Should().BeEquivalentTo(
            new[] { "anthropic", "openai", "google", "openrouter", "claude-code", "local" });

        // AuthModel mapping: claude-code → cli-token; everything else → api-key.
        providers.Single(p => p.Key == "claude-code").AuthModel.Should().Be("cli-token");
        providers.Where(p => p.Key != "claude-code")
            .Should().OnlyContain(p => p.AuthModel == "api-key");

        // Every seeded price is a v1 active seed row anchored at (or a few ticks
        // past) the seed epoch — a deterministic per-model micro-offset keeps the
        // frozen declaration order so null/"default"→first-model is reproducible.
        var prices = await ctx.ProviderModelPrices.AsNoTracking().ToListAsync();
        prices.Should().OnlyContain(p =>
            p.Status == "active" && p.Source == "seed"
            && p.EffectiveFrom >= ProviderPricingSeeder.SeedEpoch
            && p.EffectiveFrom < ProviderPricingSeeder.SeedEpoch.AddSeconds(1));
    }

    [Test]
    public async Task SeedAsync_StoresUsdPer1M_NotPerToken()
    {
        var ctx = CreateContext(nameof(SeedAsync_StoresUsdPer1M_NotPerToken));
        await ProviderPricingSeeder.SeedAsync(ctx);

        // claude-sonnet-4 is $3/1M input, $15/1M output.
        var sonnet = await ctx.ProviderModelPrices.AsNoTracking()
            .SingleAsync(p => p.ProviderKey == "anthropic" && p.Model == "claude-sonnet-4-20250514");
        sonnet.InputUsdPer1M.Should().Be(3.00m);
        sonnet.OutputUsdPer1M.Should().Be(15.00m);

        // Sub-cent rate survives the per-1M storage (gemini-1.5-flash @ $0.075/1M).
        var flash = await ctx.ProviderModelPrices.AsNoTracking()
            .SingleAsync(p => p.ProviderKey == "google" && p.Model == "gemini-1.5-flash");
        flash.InputUsdPer1M.Should().Be(0.075m);
    }

    [Test]
    public async Task SeedAsync_Is_Idempotent_SecondRun_Is_NoOp()
    {
        var ctx = CreateContext(nameof(SeedAsync_Is_Idempotent_SecondRun_Is_NoOp));

        var first = await ProviderPricingSeeder.SeedAsync(ctx);
        var providerCount = await ctx.Providers.CountAsync();
        var priceCount = await ctx.ProviderModelPrices.CountAsync();

        var second = await ProviderPricingSeeder.SeedAsync(ctx);

        second.Should().Be(0, "a re-run inserts nothing");
        (await ctx.Providers.CountAsync()).Should().Be(providerCount);
        (await ctx.ProviderModelPrices.CountAsync()).Should().Be(priceCount);
        first.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task SeedAsync_DeterministicIds_AreStable_AcrossRuns()
    {
        var a = CreateContext(nameof(SeedAsync_DeterministicIds_AreStable_AcrossRuns) + "_a");
        var b = CreateContext(nameof(SeedAsync_DeterministicIds_AreStable_AcrossRuns) + "_b");
        await ProviderPricingSeeder.SeedAsync(a);
        await ProviderPricingSeeder.SeedAsync(b);

        var idsA = (await a.ProviderModelPrices.AsNoTracking().ToListAsync())
            .ToDictionary(p => (p.ProviderKey, p.Model), p => p.Id);
        var idsB = (await b.ProviderModelPrices.AsNoTracking().ToListAsync())
            .ToDictionary(p => (p.ProviderKey, p.Model), p => p.Id);

        idsA.Should().BeEquivalentTo(idsB, "deterministic ids are stable across environments");

        // The version nibble is 7 (UUIDv7-shaped).
        foreach (var id in idsA.Values)
        {
            id.ToString("N")[12].Should().Be('7');
        }
    }

    [Test]
    public async Task SeedAsync_DoesNotRevert_AdminEditedRow()
    {
        var ctx = CreateContext(nameof(SeedAsync_DoesNotRevert_AdminEditedRow));
        await ProviderPricingSeeder.SeedAsync(ctx);

        // Simulate an admin re-price: supersede the seed row and add a new
        // active admin row with a different rate, exactly like the PUT path.
        var seedRow = await ctx.ProviderModelPrices
            .SingleAsync(p => p.ProviderKey == "anthropic" && p.Model == "claude-sonnet-4-20250514");
        seedRow.Status = "superseded";
        ctx.ProviderModelPrices.Add(new Tamma.Data.Entities.ProviderModelPrice
        {
            Id = Guid.NewGuid(),
            ProviderKey = "anthropic",
            Model = "claude-sonnet-4-20250514",
            InputUsdPer1M = 99m,
            OutputUsdPer1M = 199m,
            EffectiveFrom = DateTime.UtcNow,
            Status = "active",
            Source = "admin",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        // Re-seed must NOT re-insert the seed row (its deterministic id already
        // exists, now superseded) and must NOT touch the admin row.
        var reInserted = await ProviderPricingSeeder.SeedAsync(ctx);
        reInserted.Should().Be(0);

        var admin = await ctx.ProviderModelPrices.AsNoTracking()
            .SingleAsync(p => p.Source == "admin" && p.Model == "claude-sonnet-4-20250514");
        admin.InputUsdPer1M.Should().Be(99m, "the admin re-price survives a re-seed");
        admin.Status.Should().Be("active");
    }
}
