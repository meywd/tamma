using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Providers;
using Tamma.Data;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// Story 34-11 (AC5, AC6, AC7) — <see cref="DbProviderPricingService"/> over a
/// real Postgres testcontainer. Covers the preserved frozen-table quirks (alias
/// map, loose prefix, null/"default"→first, unknown→0m/IsKnown=false no-throw)
/// sourced from rows, and the EffectiveFrom-windowed <c>ComputeAtAsync</c>.
/// </summary>
[TestFixture]
public class DbProviderPricingServiceTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;
    private ServiceProvider _sp = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("db_pricing_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        var services = new ServiceCollection();
        services.AddDbContext<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        _sp = services.BuildServiceProvider();

        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_sp is not null) await _sp.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE provider_model_prices, providers CASCADE;");
        await ProviderPricingSeeder.SeedAsync(ctx);
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private DbProviderPricingService NewService()
    {
        var scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        var resolver = new ProviderCostResolver(
            scopeFactory, TimeProvider.System,
            NullLogger<ProviderCostResolver>.Instance, TimeSpan.Zero);
        return new DbProviderPricingService(
            scopeFactory, resolver, TimeProvider.System,
            NullLogger<DbProviderPricingService>.Instance,
            new ProviderPricingService(), TimeSpan.Zero);
    }

    [Test]
    public void Compute_KnownExactPair_ReturnsRowRate()
    {
        var svc = NewService();
        // claude-sonnet-4 @ $3/1M in, $15/1M out: 1000 in + 500 out = $0.0105.
        svc.Compute("anthropic", "claude-sonnet-4-20250514", 1000, 500)
            .Should().Be(0.0105m);
        svc.IsKnown("anthropic", "claude-sonnet-4-20250514").Should().BeTrue();
    }

    [Test]
    public void Compute_ResolvesAllFiveAliases_ToCanonicalRate()
    {
        var svc = NewService();

        // anthropic-claude / claude → anthropic
        svc.Compute("anthropic-claude", "claude-sonnet-4-20250514", 1000, 500).Should().Be(0.0105m);
        svc.Compute("claude", "claude-sonnet-4-20250514", 1000, 500).Should().Be(0.0105m);
        // gemini → google
        svc.IsKnown("gemini", "gemini-1.5-flash").Should().BeTrue();
        // github-copilot → openai
        svc.IsKnown("github-copilot", "gpt-4o").Should().BeTrue();
        // ollama / lmstudio → local
        svc.IsKnown("ollama", "local").Should().BeTrue();
        svc.IsKnown("lmstudio", "local").Should().BeTrue();
    }

    [Test]
    public void Compute_LoosePrefixMatch()
    {
        var svc = NewService();
        // "claude-sonnet-4" matches stored "claude-sonnet-4-20250514".
        svc.Compute("anthropic", "claude-sonnet-4", 1000, 500).Should().Be(0.0105m);
        svc.IsKnown("anthropic", "claude-sonnet-4").Should().BeTrue();
    }

    [Test]
    public void Compute_NullOrDefault_ResolvesToFirstModel()
    {
        var svc = NewService();
        svc.IsKnown("anthropic", null).Should().BeTrue();
        svc.IsKnown("anthropic", "default").Should().BeTrue();
        // First anthropic model is claude-sonnet-4 — same $0.0105 for 1000/500.
        svc.Compute("anthropic", null, 1000, 500).Should().Be(0.0105m);
    }

    [Test]
    public void Compute_UnknownPair_Returns0_And_IsKnownFalse_NoThrow()
    {
        var svc = NewService();
        svc.Compute("zenith-9000", "model-x", 1000, 500).Should().Be(0m);
        svc.IsKnown("zenith-9000", "model-x").Should().BeFalse();
        svc.Compute("openai", "gpt-99-nope", 1000, 500).Should().Be(0m);
        svc.IsKnown("openai", "gpt-99-nope").Should().BeFalse();
    }

    [Test]
    public void Compute_ClampsNegativeTokens_ToZero()
    {
        var svc = NewService();
        svc.Compute("anthropic", "claude-sonnet-4-20250514", -100, -200).Should().Be(0m);
    }

    [Test]
    public async Task ComputeAtAsync_PricesUnder_RateActive_AtTimestamp_NotLatest()
    {
        var svc = NewService();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Version a v2 of anthropic/claude-sonnet-4 effective at t1 ($6/1M in).
        await using (var ctx = NewContext())
        {
            var prior = await ctx.ProviderModelPrices.SingleAsync(p =>
                p.ProviderKey == "anthropic"
                && p.Model == "claude-sonnet-4-20250514"
                && p.Status == "active");
            prior.Status = "superseded";
            await ctx.SaveChangesAsync();
            ctx.ProviderModelPrices.Add(new Tamma.Data.Entities.ProviderModelPrice
            {
                Id = Guid.NewGuid(),
                ProviderKey = "anthropic",
                Model = "claude-sonnet-4-20250514",
                InputUsdPer1M = 6.00m,
                OutputUsdPer1M = 15.00m,
                EffectiveFrom = t1,
                Status = "active",
                Source = "admin",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        // Before t1 → v1 rate ($3/1M in): 1_000_000 in = $3.00.
        var before = await svc.ComputeAtAsync(
            "anthropic", "claude-sonnet-4-20250514", 1_000_000, 0,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        before.Should().Be(3.00m, "an event before the re-price uses the v1 rate");

        // At/after t1 → v2 rate ($6/1M in): 1_000_000 in = $6.00.
        var after = await svc.ComputeAtAsync(
            "anthropic", "claude-sonnet-4-20250514", 1_000_000, 0, t1);
        after.Should().Be(6.00m, "an event at the re-price uses the v2 rate");
    }

    [Test]
    public async Task ComputeAtAsync_BeforeAnyRow_Returns0()
    {
        var svc = NewService();
        // Seed epoch is 2025-01-01; anything earlier has no effective row.
        var cost = await svc.ComputeAtAsync(
            "anthropic", "claude-sonnet-4-20250514", 1000, 500,
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        cost.Should().Be(0m);
    }
}
