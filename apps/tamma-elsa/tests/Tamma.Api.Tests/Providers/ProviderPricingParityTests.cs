using System.Collections.Frozen;
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
/// Story 34-11 (AC12 — THE load-bearing test) — for every seeded
/// <c>(provider, model)</c> pair, the DB-backed <see cref="DbProviderPricingService"/>
/// produces byte-identical <c>Compute</c> output to the frozen
/// <see cref="ProviderPricingService"/>. Proves the promotion behind the
/// <see cref="IProviderPricingService"/> seam is behaviour-preserving — incl.
/// sub-cent rates and the integer-token math.
/// </summary>
[TestFixture]
public class ProviderPricingParityTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;
    private ServiceProvider _sp = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("parity_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        var services = new ServiceCollection();
        services.AddDbContext<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        _sp = services.BuildServiceProvider();

        await using var ctx = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);
        await ctx.Database.MigrateAsync();
        await ProviderPricingSeeder.SeedAsync(ctx);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_sp is not null) await _sp.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private DbProviderPricingService NewDbService()
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

    /// <summary>
    /// Every (provider, model) in the frozen table — with a few token tuples
    /// covering small, large, and asymmetric usage so rounding diverges if the
    /// per-1M ↔ per-token round-trip is lossy.
    /// </summary>
    private static IEnumerable<TestCaseData> SeededPairs()
    {
        var frozen = ProviderPricingService.Pricing;
        var tuples = new (int In, int Out)[] { (1000, 500), (1_000_000, 1_000_000), (12345, 0), (0, 9999) };

        foreach (var (provider, models) in frozen)
        {
            foreach (var (model, _) in models)
            {
                foreach (var (inTok, outTok) in tuples)
                {
                    yield return new TestCaseData(provider, model, inTok, outTok)
                        .SetName($"Parity_{provider}_{model.Replace('/', '_').Replace('.', '_')}_{inTok}_{outTok}");
                }
            }
        }
    }

    [TestCaseSource(nameof(SeededPairs))]
    public void DbCompute_Matches_FrozenCompute_ByteForByte(
        string provider, string model, int inTok, int outTok)
    {
        var frozen = new ProviderPricingService();
        var db = NewDbService();

        var frozenCost = frozen.Compute(provider, model, inTok, outTok);
        var dbCost = db.Compute(provider, model, inTok, outTok);

        dbCost.Should().Be(frozenCost,
            $"DB and frozen Compute must agree for {provider}/{model} ({inTok} in, {outTok} out)");
    }

    [Test]
    public void IsKnown_Matches_Frozen_ForEverySeededPair()
    {
        var frozen = new ProviderPricingService();
        var db = NewDbService();

        foreach (var (provider, models) in ProviderPricingService.Pricing)
        {
            foreach (var (model, _) in models)
            {
                db.IsKnown(provider, model).Should().Be(frozen.IsKnown(provider, model),
                    $"IsKnown must agree for {provider}/{model}");
            }
        }
    }
}
