using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Stripe;
using Stripe.Billing;
using Tamma.Api.Services.Billing;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 (AC7, AC8) — <see cref="BillingSeeder"/> idempotently syncs the
/// Stripe catalog: first run creates Products / base + metered Prices / the
/// three Billing Meters (with the right SUM/SUM/LAST aggregation) and writes
/// their ids into <c>billing_plan_prices</c>; a second run finds existing ids
/// and makes ZERO create calls (no row churn). One
/// <c>BILLING.PLAN_CATALOG.SYNCED</c> is emitted per slug. All Stripe services
/// are mocked at the service-interface boundary.
/// </summary>
[TestFixture]
public class BillingSeederTests
{
    private sealed class Harness
    {
        public Mock<ProductService> Products { get; } = new();
        public Mock<PriceService> Prices { get; } = new();
        public Mock<MeterService> Meters { get; } = new();
        public Mock<IEventRepository> Events { get; } = new();
        public List<MeterCreateOptions> MeterCalls { get; } = new();

        public int ProductCreates;
        public int PriceCreates;
        public int MeterCreates;

        public IStripeServices Services { get; }

        public Harness()
        {
            var bundle = new Mock<IStripeServices>();
            bundle.SetupGet(s => s.Products).Returns(Products.Object);
            bundle.SetupGet(s => s.Prices).Returns(Prices.Object);
            bundle.SetupGet(s => s.Meters).Returns(Meters.Object);
            // Customers unused by the seeder.
            Services = bundle.Object;

            Products.Setup(p => p.CreateAsync(
                    It.IsAny<ProductCreateOptions>(), It.IsAny<RequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProductCreateOptions o, RequestOptions _, CancellationToken _) =>
                {
                    ProductCreates++;
                    return new Product { Id = o.Id ?? $"prod_{Guid.NewGuid():N}" };
                });

            Prices.Setup(p => p.CreateAsync(
                    It.IsAny<PriceCreateOptions>(), It.IsAny<RequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    PriceCreates++;
                    return new Price { Id = $"price_{Guid.NewGuid():N}" };
                });

            Meters.Setup(m => m.CreateAsync(
                    It.IsAny<MeterCreateOptions>(), It.IsAny<RequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((MeterCreateOptions o, RequestOptions _, CancellationToken _) =>
                {
                    MeterCreates++;
                    MeterCalls.Add(o);
                    return new Meter { Id = $"mtr_{Guid.NewGuid():N}" };
                });

            Events.Setup(e => e.AppendAsync(It.IsAny<DomainEvent>()))
                .ReturnsAsync((DomainEvent d) => d);
        }

        public BillingSeeder NewSeeder(ControlPlaneDbContext db) =>
            new(Services, db, Events.Object, new BillingOptions(),
                NullLogger.Instance);
    }

    private static ControlPlaneDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    [Test]
    public async Task FirstRun_Creates_Catalog_And_Three_Meters()
    {
        var h = new Harness();
        await using var db = NewDb(nameof(FirstRun_Creates_Catalog_And_Three_Meters));

        var result = await h.NewSeeder(db).SyncAsync();

        // Three slugs each get a product + base price + 3 metered prices; the
        // three meters are created once and shared.
        result.Slugs.Select(s => s.PlanSlug).Should()
            .BeEquivalentTo(new[] { "free", "team", "enterprise" });
        h.MeterCreates.Should().Be(3, "the three platform meters are created once");
        h.ProductCreates.Should().Be(3, "one product per slug");
        h.PriceCreates.Should().Be(12, "one base + three metered prices per slug (4 × 3)");

        var rows = await db.BillingPlanPrices.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r =>
            r.StripeProductId != null && r.StripePriceId != null
            && r.TokensInputMeterId != null && r.TokensOutputMeterId != null && r.SeatsMeterId != null
            && r.TokensInputPriceId != null && r.TokensOutputPriceId != null && r.SeatsPriceId != null);

        h.Events.Verify(e => e.AppendAsync(
                It.Is<DomainEvent>(d => d.Type == BillingEvents.PlanCatalogSyncedType)),
            Times.Exactly(3), "one BILLING.PLAN_CATALOG.SYNCED per slug");
    }

    [Test]
    public async Task Meters_Use_Correct_Aggregation_Formulas()
    {
        var h = new Harness();
        await using var db = NewDb(nameof(Meters_Use_Correct_Aggregation_Formulas));

        await h.NewSeeder(db).SyncAsync();

        var byEvent = h.MeterCalls.ToDictionary(c => c.EventName, c => c.DefaultAggregation.Formula);
        byEvent.Should().ContainKey("tamma.platform_tokens_input").WhoseValue.Should().Be("sum");
        byEvent.Should().ContainKey("tamma.platform_tokens_output").WhoseValue.Should().Be("sum");
        byEvent.Should().ContainKey("tamma.seats").WhoseValue.Should().Be("last");
    }

    [Test]
    public async Task SecondRun_Is_NoOp_Reuses_Existing_Ids()
    {
        var dbName = nameof(SecondRun_Is_NoOp_Reuses_Existing_Ids);

        // First run populates the catalog.
        var first = new Harness();
        await using (var db = NewDb(dbName))
        {
            await first.NewSeeder(db).SyncAsync();
        }

        // Second run against the SAME in-memory DB sees the populated rows.
        var second = new Harness();
        await using (var db = NewDb(dbName))
        {
            var result = await second.NewSeeder(db).SyncAsync();
            result.TotalCreated.Should().Be(0, "everything is reused on the second run");
            result.TotalReused.Should().BeGreaterThan(0);
        }

        second.ProductCreates.Should().Be(0);
        second.PriceCreates.Should().Be(0);
        second.MeterCreates.Should().Be(0, "the meters were stored — no re-create");

        await using var verify = NewDb(dbName);
        (await verify.BillingPlanPrices.CountAsync()).Should().Be(3, "no row churn");
    }
}
