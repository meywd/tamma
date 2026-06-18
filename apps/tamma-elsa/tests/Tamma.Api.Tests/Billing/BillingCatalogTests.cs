using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Billing;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 — <see cref="BillingCatalog"/> resolves a <see cref="BillingPlanPrice"/>
/// by slug, throws a fail-loud error on an unknown slug (never silent null on
/// the strict path), and caches a hit.
/// </summary>
[TestFixture]
public class BillingCatalogTests
{
    private ServiceProvider _sp = null!;
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;
    private string _dbName = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = $"billing-catalog-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlPlaneDbContext>(
            opts => opts.UseInMemoryDatabase(_dbName));
        _sp = services.BuildServiceProvider();
        _factory = _sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
    }

    [TearDown]
    public void TearDown() => _sp.Dispose();

    private async Task SeedAsync(string slug)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.BillingPlanPrices.Add(new BillingPlanPrice
        {
            Id = Guid.NewGuid(),
            PlanSlug = slug,
            StripeProductId = $"prod_{slug}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task GetBySlugAsync_Returns_Known_Slug()
    {
        await SeedAsync("team");
        var catalog = new BillingCatalog(_factory);

        var row = await catalog.GetBySlugAsync("team");

        row.PlanSlug.Should().Be("team");
        row.StripeProductId.Should().Be("prod_team");
    }

    [Test]
    public async Task GetBySlugAsync_Unknown_Slug_Throws()
    {
        var catalog = new BillingCatalog(_factory);

        var act = async () => await catalog.GetBySlugAsync("nope");

        var ex = await act.Should().ThrowAsync<TammaError>();
        ex.Which.Code.Should().Be("BILLING.CATALOG.UNKNOWN_SLUG");
    }

    [Test]
    public async Task TryGetBySlugAsync_Unknown_Slug_Returns_Null()
    {
        var catalog = new BillingCatalog(_factory);
        (await catalog.TryGetBySlugAsync("nope")).Should().BeNull();
    }

    [Test]
    public async Task GetBySlugAsync_Caches_Hit_No_Second_Db_Read()
    {
        await SeedAsync("free");
        var catalog = new BillingCatalog(_factory);

        var first = await catalog.GetBySlugAsync("free");

        // Mutate the underlying row; a cached read must still return the old value
        // within the TTL (proves the cache served the second call).
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.BillingPlanPrices.SingleAsync(r => r.PlanSlug == "free");
            row.StripeProductId = "prod_changed";
            await db.SaveChangesAsync();
        }

        var second = await catalog.GetBySlugAsync("free");
        second.StripeProductId.Should().Be(first.StripeProductId)
            .And.Be("prod_free", "the cached entry is served within the TTL");
    }
}
